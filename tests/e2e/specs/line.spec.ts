import type { Page } from "@playwright/test";
import { line as lineApi } from "../support/api";
import { expect, test } from "../support/fixtures";
import { button, countRequests, panel, settle } from "../support/ui";

async function openLine(page: Page): Promise<void> {
  await page.goto("/dashboard");
  await expect(page.getByRole("heading", { level: 1, name: "Dashboard" })).toBeVisible();
  await expect(panel(page, "Production line").getByLabel("Requested rate")).toBeVisible();
}

function count(page: Page, label: string) {
  return panel(page, "Production line").locator("dt", { hasText: new RegExp(`^${label}$`) }).locator("+ dd");
}

test.describe("production line", () => {
  test("REQ-BT-031 TM-681 start, change rate, stop, and the walks finish", async ({ actor, line }) => {
    test.setTimeout(150_000);
    await line.stop();
    await lineApi.quiet();
    const page = await actor("ada");
    await openLine(page);
    const before = await lineApi.status();
    const lineControls = panel(page, "Production line");
    await expect(lineControls.getByText("Stopped", { exact: true })).toBeVisible();

    await lineControls.getByLabel("Requested rate").fill("1");
    await button(page, "Start line").click();
    await expect(lineControls.getByText("Running, 1 per second requested")).toBeVisible();
    await expect.poll(async () => Number(await count(page, "Started").innerText()), { timeout: 20_000 }).toBeGreaterThan(before.started);

    await lineControls.getByLabel("Requested rate").fill("2");
    await expect(lineControls.getByText("Running, 2 per second requested")).toBeVisible();
    expect((await lineApi.status()).rate).toBe(2);

    await button(page, "Stop line").click();
    await expect(lineControls.getByText("Stopped", { exact: true })).toBeVisible();
    expect((await lineApi.status()).running).toBe(false);

    await lineApi.quiet();
    const end = await lineApi.status();
    expect(end.closed + end.rejected, "stop leaves walks to finish").toBeGreaterThan(before.closed + before.rejected);
    expect(end.errors).toBe(before.errors);
    await button(lineControls.page(), "Refresh").first().click();
    await expect(count(page, "Closed")).toHaveText(end.closed.toLocaleString("en-US"));
  });

  test("REQ-BT-031 TM-682 the lever stops at 35 and a stopped line stays unchanged", async ({ actor, line }) => {
    await line.stop();
    await lineApi.quiet();
    const page = await actor("ada");
    await openLine(page);
    const writes = countRequests(page, "POST", /\/line$/);
    const before = await lineApi.status();
    const field = panel(page, "Production line").getByLabel("Requested rate");
    await expect(field).toHaveAttribute("max", "35");
    await field.fill("35");
    await field.press("ArrowRight");
    await expect(field).toHaveValue("35");
    await field.press("ArrowLeft");
    await expect(field).toHaveValue("34.8");
    expect(writes.count).toBe(0);
    expect(await lineApi.status()).toEqual(before);
    await page.reload();
    await expect(panel(page, "Production line").getByText("Stopped", { exact: true })).toBeVisible();
  });

  test("REQ-BT-031 TM-3726 a drag of the lever sends one rate, on release", async ({ actor, line }) => {
    await line.stop();
    await lineApi.quiet();
    const page = await actor("ada");
    await openLine(page);
    const lineControls = panel(page, "Production line");
    await lineControls.getByLabel("Requested rate").fill("1");
    await button(page, "Start line").click();
    await expect(lineControls.getByText("Running, 1 per second requested")).toBeVisible();
    const writes = countRequests(page, "POST", /\/line$/);
    const box = (await lineControls.getByLabel("Requested rate").boundingBox())!;
    const y = box.y + box.height / 2;
    await page.mouse.move(box.x + box.width * 0.03, y);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width * 0.5, y, { steps: 20 });
    expect(writes.count).toBe(0);
    await page.mouse.up();
    await expect(lineControls.getByText(/Running, 1[5-9](\.\d)? per second requested/)).toBeVisible();
    expect(writes.count).toBe(1);
    await button(page, "Stop line").click();
    await expect(lineControls.getByText("Stopped", { exact: true })).toBeVisible();
    await lineApi.quiet();
  });

  test("REQ-BT-031 TM-683 rate 0 reads idle, then rate 5 reads running", async ({ actor, line }) => {
    await line.stop();
    await lineApi.quiet();
    const page = await actor("ada");
    await openLine(page);
    const lineControls = panel(page, "Production line");
    await lineControls.getByLabel("Requested rate").fill("0");
    await button(page, "Start line").click();
    await expect(lineControls.getByText("Idle: rate is 0")).toBeVisible();
    const idle = await lineApi.status();
    expect(idle.running).toBe(true);
    expect(idle.rate).toBe(0);

    await lineControls.getByLabel("Requested rate").fill("5");
    await expect(lineControls.getByText("Running, 5 per second requested")).toBeVisible();
    await page.reload();
    await expect(panel(page, "Production line").getByText("Running, 5 per second requested")).toBeVisible();
    await button(page, "Stop line").click();
    await expect(panel(page, "Production line").getByText("Stopped", { exact: true })).toBeVisible();
    await lineApi.quiet();
    await settle(page);
  });
});
