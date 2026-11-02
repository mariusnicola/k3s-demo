import { api, line as lineApi, type Batch } from "../support/api";
import { expect, test } from "../support/fixtures";
import { panel, settle } from "../support/ui";

function entries(page: import("@playwright/test").Page) {
  return page.getByRole("table", { name: "Log entries" });
}

test.describe("logs", () => {
  test("REQ-BT-055 TM-2125 olivia opens /logs and lands on her dashboard, with no Logs link", async ({ actor }) => {
    const page = await actor("olivia");
    await page.goto("/logs");
    await expect(page.getByRole("region", { name: "Batches by step" })).toBeVisible();
    await expect(page).toHaveURL(/\/dashboard$/);
    await expect(page.getByRole("link", { name: "Logs" })).toHaveCount(0);
  });

  test("REQ-BT-045 TM-2126 a batch link in an entry opens that batch", async ({ actor, line }) => {
    test.setTimeout(120_000);
    await line.start(1);
    await expect.poll(async () => (await lineApi.status()).started, { timeout: 20_000 }).toBeGreaterThan(0);
    await line.stop();
    await lineApi.quiet();
    const page = await actor("ada");
    await page.goto("/logs?level=info");
    await expect(page.getByRole("heading", { level: 1, name: "Logs" })).toBeVisible();
    const link = entries(page).getByRole("link", { name: /^Batch / }).first();
    await expect(link).toBeVisible();
    const href = (await link.getAttribute("href"))!;
    await link.click();
    await expect(page).toHaveURL(new RegExp(`${href}$`));
    await expect(page.getByRole("region", { name: "Next step" })).toBeVisible();
  });

  test("REQ-BT-045 TM-2124 a real failure on the line: Errors opens Logs filtered, entry shows reason, step, batch, details", async ({ actor, line }) => {
    test.setTimeout(240_000);
    await line.stop();
    await lineApi.quiet();
    const before = await lineApi.status();

    // Fail next dead-letters a line batch, so its walk waits out the Line's own event timeout. Nothing is stopped.
    await line.start(1);
    let target: Batch | undefined;
    await expect
      .poll(
        async () => {
          target = (await api.list("ada", 20)).find((batch) => batch.product.startsWith("Line batch") && batch.state === "Created" && batch.pending !== null);
          return Boolean(target);
        },
        { timeout: 30_000, message: "the line never made a batch to fail" },
      )
      .toBe(true);
    await line.stop();
    await api.failNext(target!);
    await api.dead(target!);

    try {
      await expect.poll(async () => (await lineApi.status()).errors, { timeout: 120_000, intervals: [2_000], message: "the Line never reported the failure" }).toBeGreaterThan(before.errors);
      await lineApi.quiet();

      const page = await actor("ada");
      await page.goto("/dashboard");
      await settle(page);
      const errors = (await lineApi.status()).errors;
      await panel(page, "Production line").getByRole("link", { name: `${errors.toLocaleString("en-US")} errors, open the logs` }).click();
      await expect(page).toHaveURL(/\/logs\?level=error&source=line$/);
      await expect(page.getByRole("heading", { level: 1, name: "Logs" })).toBeVisible();
      await expect(page.getByLabel("Level")).toHaveValue("error");

      const row = entries(page).locator("tbody tr").filter({ hasText: "The Line gave up waiting for the previous event of a batch." }).first();
      await expect(row).toBeVisible();
      await expect(row.getByRole("link", { name: /^Batch / })).toHaveAttribute("href", `/batches/${target!.farmId}/${target!.id}`);
      await expect(row.locator('td[data-label="Step"]')).not.toBeEmpty();
      await entries(page).getByRole("button", { name: /details of event-timeout/ }).first().click();
      await expect(page.getByText("Reason code")).toBeVisible();
      await expect(page.locator("dd").filter({ hasText: /^event-timeout$/ })).toBeVisible();
      await expect(page.locator("dd").filter({ hasText: target!.id }).first()).toBeVisible();
    } finally {
      await api.replay(target!);
      await api.settled(target!);
    }
  });
});
