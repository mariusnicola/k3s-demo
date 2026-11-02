import type { Page } from "@playwright/test";
import { api, line as lineApi } from "../support/api";
import { expect, test } from "../support/fixtures";
import { bodyRows, button, panel, settle } from "../support/ui";

async function openDashboard(page: Page): Promise<void> {
  await page.goto("/dashboard");
  await expect(page.getByRole("heading", { level: 1, name: "Dashboard" })).toBeVisible();
  await settle(page);
}

/** The Performance panel is not in the source yet. Its region and control names here come from REQ-BT-046 and are to be matched when it lands. */
function performance(page: Page) {
  return panel(page, "Performance");
}

function bucket(page: Page, name: string) {
  return performance(page).getByRole("radio", { name, exact: true });
}

test.describe("dashboard feeds", () => {
  test("REQ-BT-042 TM-1982 Dashboard opened after changes happened shows the kept list", async ({ actor }) => {
    const page = await actor("ada");
    await openDashboard(page);
    expect(await bodyRows(panel(page, "Changes").getByRole("table", { name: "Changes" })).count()).toBeGreaterThan(0);
  });

  test("REQ-BT-044 TM-2037 Dashboard on the live cluster shows pod network checks healthy", async ({ actor }) => {
    const page = await actor("ada");
    await openDashboard(page);
    const rows = bodyRows(panel(page, "Health").getByRole("table", { name: "Health checks" }));
    const pod = rows.filter({ hasText: /^pod (dns|net) / });
    expect(await pod.count(), "pod dns and pod net checks exist").toBeGreaterThan(1);
    await expect(pod.filter({ hasNotText: "Healthy" })).toHaveCount(0);
  });

  test("REQ-BT-028 TM-1131 a failed dot on the page shows the diamond, on the failed dot only", async ({ actor }) => {
    const page = await actor("ada");
    const batch = await api.batchAt("Created");
    await api.failNext(batch);
    await openDashboard(page);
    await page.evaluate(() => {
      const win = window as unknown as { __shapes: { ok: string; core: string; diamond: string }[] };
      win.__shapes = [];
      const sample = () => {
        for (const dot of document.querySelectorAll(".ds-dot[visibility=visible]")) {
          const shown = (selector: string) => getComputedStyle(dot.querySelector(selector)!).display;
          win.__shapes.push({ ok: dot.getAttribute("data-ok") ?? "", core: shown(".ds-dot-core"), diamond: shown(".ds-dot-fail") });
        }
        requestAnimationFrame(sample);
      };
      requestAnimationFrame(sample);
    });
    await api.advance("olivia", batch, "Dose1");
    await api.dead(batch);
    const shapes = () => page.evaluate(() => (window as unknown as { __shapes: { ok: string; core: string; diamond: string }[] }).__shapes);
    await expect.poll(async () => (await shapes()).filter((shape) => shape.ok === "false").length, { message: "a failed hop must draw" }).toBeGreaterThan(0);
    const seen = await shapes();
    expect(seen.filter((shape) => shape.ok === "false" && (shape.diamond === "none" || shape.core !== "none"))).toEqual([]);
    expect(seen.filter((shape) => shape.ok === "true" && shape.diamond !== "none")).toEqual([]);
    await api.replay(batch);
    await api.settled(batch);
  });

  test("REQ-BT-031 TM-2219 rate 35, the top of the lever, runs the line at that rate", async ({ actor, line }) => {
    test.setTimeout(150_000);
    await line.stop();
    await lineApi.quiet();
    const page = await actor("ada");
    await page.goto("/dashboard");
    const controls = panel(page, "Production line");
    await controls.getByLabel("Requested rate").fill("35");
    await button(page, "Start line").click();
    await expect(controls.getByText("Running, 35 per second requested")).toBeVisible();
    await line.stop();
    await expect.poll(async () => (await lineApi.status()).running).toBe(false);
    await lineApi.quiet();
  });

  test("REQ-BT-046 TM-2162 line runs, bucket changed, page reloaded: numbers move and the bucket is remembered", async ({ actor, line }) => {
    test.setTimeout(240_000);
    const page = await actor("ada");
    await openDashboard(page);
    await bucket(page, "1m").check();
    const before = await performance(page).innerText();
    await line.start(1);
    await expect.poll(() => performance(page).innerText(), { timeout: 120_000, intervals: [2_000], message: "numbers must move at the next whole minute" }).not.toBe(before);
    await line.stop();
    await lineApi.quiet();
    await bucket(page, "30m").check();
    await page.reload();
    await expect(bucket(page, "30m")).toBeChecked();
  });

  test("REQ-BT-046 TM-2164 a second browser keeps its own remembered bucket", async ({ actor }) => {
    const first = await actor("ada");
    const second = await actor("ada");
    await openDashboard(first);
    await openDashboard(second);
    await bucket(first, "1h").check();
    await bucket(second, "3h").check();
    await first.reload();
    await second.reload();
    await expect(bucket(first, "1h")).toBeChecked();
    await expect(bucket(second, "3h")).toBeChecked();
  });
});
