import type { Page } from "@playwright/test";
import { api, line as lineApi } from "../support/api";
import { expect, test } from "../support/fixtures";
import { bodyRows, button, clippedControls, expectNoSideScroll, panel, settle } from "../support/ui";

async function openDashboard(page: Page): Promise<void> {
  await page.goto("/dashboard");
  await expect(page.getByRole("heading", { level: 1, name: "Dashboard" })).toBeVisible();
}

function playbackLine(page: Page) {
  return panel(page, "Architecture").getByText(/Playback at /);
}

async function hopsToPlay(page: Page): Promise<number> {
  const text = await playbackLine(page).innerText();
  return Number(text.match(/([\d,]+) hops still to play/)![1]!.replace(/,/g, ""));
}

async function hopsSeen(page: Page): Promise<number> {
  const text = await panel(page, "Architecture").getByText(/hops seen in this view/).innerText();
  return Number(text.match(/([\d,]+) hops seen in this view/)![1]!.replace(/,/g, ""));
}

/** A burst of real hops, dense enough that playback pace can be measured. */
async function burst(line: { start: (rate: number) => Promise<void>; stop: () => Promise<void> }, hops: number, page: Page): Promise<void> {
  await line.start(5);
  await expect.poll(() => hopsSeen(page), { timeout: 40_000 }).toBeGreaterThan(hops);
  await line.stop();
  await lineApi.quiet();
}

test.describe("dashboard", () => {
  test("REQ-BT-015 TM-698 ada opens Dashboard and all panels load", async ({ actor }) => {
    const page = await actor("ada");
    await openDashboard(page);
    await settle(page);
    for (const name of ["Architecture", "Production line", "Nightly compliance", "Failure injection", "Health", "Recent hops", "Nodes", "Caps", "Pods"]) {
      await expect(panel(page, name)).toBeVisible();
    }
    await expect(page.getByText("Hop stream: live")).toBeVisible();
    await expect(page.getByText("Monitor: live")).toBeVisible();
  });

  test("REQ-BT-026 TM-379 ada sees caps, node usage and pods from the live monitor", async ({ actor }) => {
    const page = await actor("ada");
    await openDashboard(page);
    await expect(page.getByText("Monitor: live")).toBeVisible({ timeout: 40_000 });
    await expect(panel(page, "Health").getByText(/Checked /)).toBeVisible();
    await expect(panel(page, "Caps").getByRole("meter").first()).toBeVisible();
    expect(await panel(page, "Caps").getByRole("meter").count()).toBeGreaterThan(1);
    const nodes = bodyRows(panel(page, "Nodes").getByRole("table", { name: "Nodes" }));
    expect(await nodes.count()).toBeGreaterThan(0);
    // The control plane is no Kubernetes node, so it reports no allocatable memory.
    await expect(nodes.filter({ hasText: "k3s-node2" }).locator('td[data-label="Memory used of allocatable"]')).toHaveText(/^[\d,.]+ ?Mi of [\d,.]+ ?Mi$/);
    const pods = bodyRows(panel(page, "Pods").getByRole("table", { name: "Pods" }));
    expect(await pods.count()).toBeGreaterThan(0);
    await expect(pods.first().locator('td[data-label="Phase"]')).toHaveText(/\w+/);
  });

  test("REQ-BT-026 TM-381 Dashboard at 360 px stacks the cap and pod tables with no sideways scroll", async ({ actor }) => {
    const page = await actor("ada", { viewport: { width: 360, height: 800 } });
    await openDashboard(page);
    await expect(page.getByText("Monitor: live")).toBeVisible({ timeout: 40_000 });
    await settle(page);
    await expectNoSideScroll(page);
    expect(await clippedControls(page)).toEqual([]);
    const cell = panel(page, "Pods").locator('td[data-label="Pod"]').first();
    expect(await cell.evaluate((el) => getComputedStyle(el).display)).toBe("grid");
    const meter = panel(page, "Caps").getByRole("meter").first();
    const box = (await meter.boundingBox())!;
    expect(box.x + box.width).toBeLessThanOrEqual(360);
  });

  test("REQ-BT-013 TM-058 nearest: ada runs nightly and the dashboard says it finished", async ({ actor }) => {
    const page = await actor("ada");
    await openDashboard(page);
    await button(page, "Run nightly now").click();
    await expect(panel(page, "Nightly compliance").getByText(/^Nightly run finished at /)).toBeVisible();
  });

  test("REQ-BT-028 TM-610 ada advances a batch and follows its trace across all six stations", async ({ actor }) => {
    const batch = await api.batchAt("Created");
    const page = await actor("ada");
    await openDashboard(page);
    await expect(page.getByText("Hop stream: live")).toBeVisible();
    await settle(page);
    await page.evaluate(() => {
      const win = window as unknown as { __dots: Record<string, string[]> };
      win.__dots = {};
      const sample = () => {
        for (const dot of document.querySelectorAll(".ds-dot[visibility=visible]")) {
          const trace = dot.getAttribute("data-trace") ?? "";
          const at = dot.getAttribute("transform") ?? "";
          const seen = (win.__dots[trace] ??= []);
          if (seen.at(-1) !== at) seen.push(at);
        }
        requestAnimationFrame(sample);
      };
      requestAnimationFrame(sample);
    });

    await api.advance("olivia", batch, "Dose1");
    const { batch: stored } = await api.settled(batch);
    const trace = stored.steps.at(-1)!.traceId;

    await expect.poll(() => page.evaluate((id) => (window as unknown as { __dots: Record<string, string[]> }).__dots[id]?.length ?? 0, trace), { message: "a dot must move for this trace" }).toBeGreaterThan(5);

    await page.getByRole("button", { name: `Follow trace ${trace}` }).first().click();
    await expect(page.getByText(/^Following trace /)).toBeVisible();
    const services = page.getByRole("table", { name: "Recent hops" }).locator('td[data-label="Service"]');
    await expect(services).toHaveCount(6);
    expect((await services.allInnerTexts()).slice(0, 4)).toEqual(["Record written", "Worker", "Service Bus", "Dispatcher"]);
    // API and Store carry the same instant, so either may arrive first.
    expect((await services.allInnerTexts()).slice(4).sort()).toEqual(["API", "Store"]);
    await expect(page.getByRole("table", { name: "Recent hops" }).getByText("Failed")).toHaveCount(0);
  });

  test("REQ-BT-028 TM-612 line at rate 5 moves the counts and the page stays usable", async ({ actor, line }) => {
    const page = await actor("ada");
    await openDashboard(page);
    await expect(page.getByText("Hop stream: live")).toBeVisible();
    await settle(page);
    const before = await hopsSeen(page);
    await line.start(5);
    await expect.poll(() => hopsSeen(page), { timeout: 30_000 }).toBeGreaterThan(before + 100);
    await expect(page.getByText("Running, 5 per second requested")).toBeVisible();
    await button(page, "Freeze list").click();
    await expect(button(page, "Resume list")).toBeVisible();
    await button(page, "Resume list").click();
    await page.getByRole("link", { name: "Alerts" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Alerts" })).toBeVisible();
    await page.getByRole("link", { name: "Dashboard" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Dashboard" })).toBeVisible();
    await line.stop();
    await lineApi.quiet();
  });

  test("REQ-BT-029 TM-632 RealTime on then off plays the buffered hops", async ({ actor, line }) => {
    const page = await actor("ada");
    await openDashboard(page);
    await expect(page.getByText("Hop stream: live")).toBeVisible();
    await settle(page);
    await burst(line, 60, page);

    await page.getByRole("checkbox", { name: "RealTime Monitor" }).uncheck();
    await expect(page.getByText("Dots replay from the buffer.")).toBeVisible();
    await expect(playbackLine(page)).toBeVisible();
    const start = await hopsToPlay(page);
    await expect.poll(() => hopsToPlay(page), { message: "playback must consume the buffer" }).toBeLessThan(start);
    await expect.poll(() => hopsSeen(page), { message: "played hops reach the counts" }).toBeGreaterThan(0);
  });

  test("REQ-BT-029 TM-633 each speed changes the pace and the position moves", async ({ actor, line }) => {
    test.setTimeout(180_000);
    const page = await actor("ada");
    await openDashboard(page);
    await expect(page.getByText("Hop stream: live")).toBeVisible();
    await settle(page);
    await burst(line, 200, page);
    await page.getByRole("checkbox", { name: "RealTime Monitor" }).uncheck();

    const take = 40;
    const paces: Record<string, number> = {};
    for (const speed of ["2x", "1x", "0.5x", "0.25x"]) {
      await page.getByRole("radio", { name: speed, exact: true }).check({ force: true });
      await button(page, "Restart playback").click();
      await expect(playbackLine(page)).toContainText(`Playback at ${speed}`);
      const position = (await playbackLine(page).innerText()).match(/position ([^,]+),/)![1];
      const start = await hopsToPlay(page);
      const from = Date.now();
      await expect.poll(() => hopsToPlay(page), { timeout: 60_000, intervals: [50] }).toBeLessThanOrEqual(start - take);
      paces[speed] = Date.now() - from;
      expect((await playbackLine(page).innerText()).match(/position ([^,]+),/)![1], `position moved at ${speed}`).not.toBe(position);
    }
    expect(paces["2x"]!, JSON.stringify(paces)).toBeLessThan(paces["1x"]!);
    expect(paces["1x"]!, JSON.stringify(paces)).toBeLessThan(paces["0.5x"]!);
    expect(paces["0.5x"]!, JSON.stringify(paces)).toBeLessThan(paces["0.25x"]!);
  });

  test("REQ-BT-029 TM-634 speed with RealTime on is locked with the reason", async ({ actor }) => {
    const page = await actor("ada");
    await openDashboard(page);
    await expect(page.getByRole("checkbox", { name: "RealTime Monitor" })).toBeChecked();
    for (const speed of ["0.25x", "0.5x", "1x", "2x"]) await expect(page.getByRole("radio", { name: speed, exact: true })).toBeDisabled();
    await expect(page.getByText("Turn RealTime Monitor off to choose a speed.")).toBeVisible();
    await page.getByRole("radio", { name: "2x", exact: true }).click({ force: true });
    await expect(page.getByRole("radio", { name: "1x", exact: true })).toBeChecked();
    await expect(button(page, "Restart playback")).toHaveCount(0);
  });

  test("REQ-BT-029 TM-635 hub lost during playback keeps playing and says the stream is lost", async ({ relayed, guard }) => {
    const page = await relayed.actor("ada");
    await openDashboard(page);
    await expect(page.getByText("Hop stream: live")).toBeVisible();
    await settle(page);
    const batch = await api.batchAt("Dose2");
    await api.inspect(batch, true);
    await api.settled(batch);
    await page.getByRole("checkbox", { name: "RealTime Monitor" }).uncheck();
    await expect(playbackLine(page)).toBeVisible();

    const end = guard.expectSocketLoss();
    relayed.relay.dropSockets();
    await expect(page.getByText("Playing the buffer only: the hop stream is lost")).toBeVisible();
    await expect(page.getByText("Dots stopped")).toHaveCount(0);
    await expect(page.getByText("Hop stream: connection lost")).toBeVisible();
    await button(page, "Restart playback").click();
    const start = await hopsToPlay(page);
    await expect.poll(() => hopsToPlay(page), { message: "playback continues with no stream" }).toBeLessThan(start);
    relayed.relay.restore();
    await expect(page.getByText("Hop stream: live")).toBeVisible({ timeout: 40_000 });
    end();
  });

  test("REQ-BT-030 TM-667 network offline for a minute reads lost, then returns with no double count", async ({ relayed, guard }) => {
    test.setTimeout(180_000);
    const page = await relayed.actor("ada");
    await openDashboard(page);
    await expect(page.getByText("Hop stream: live")).toBeVisible();
    await settle(page);
    const batch = await api.batchAt("Created");
    await api.advance("olivia", batch, "Dose1");
    await api.settled(batch);
    await expect.poll(() => hopsSeen(page)).toBeGreaterThan(0);
    await expect.poll(async () => {
      const first = await hopsSeen(page);
      await page.waitForTimeout(1_000);
      return first === (await hopsSeen(page));
    }).toBe(true);
    const before = await hopsSeen(page);

    const end = guard.expectOutage();
    relayed.relay.cutAll();
    await expect(page.getByText("Hop stream: connection lost")).toBeVisible();
    await expect(page.getByText(/Last data .* ago\. Retrying\./).first()).toBeVisible();
    // The row asks for a full minute of outage, so this wait is the case itself.
    await page.waitForTimeout(60_000);
    await expect(page.getByText("Hop stream: connection lost")).toBeVisible();
    relayed.relay.restore();
    await expect(page.getByText("Hop stream: live")).toBeVisible({ timeout: 45_000 });
    await expect(page.getByText("Monitor: live")).toBeVisible({ timeout: 45_000 });
    await settle(page);
    end();
    expect(await hopsSeen(page), "the hub resend must not count twice").toBe(before);
  });
});
