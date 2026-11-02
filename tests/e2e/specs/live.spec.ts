import type { Page } from "@playwright/test";
import { api, line as lineApi, productName } from "../support/api";
import { expect, test } from "../support/fixtures";
import { batchesTable, chooseUser, settle } from "../support/ui";

const OBSERVE_MS = 60_000;

function trackRequests(page: Page, into: string[]): void {
  page.on("request", (request) => into.push(`${request.method()} ${request.url()}`));
}

test.describe("live data, no polling", () => {
  test("REQ-BT-030 TM-664 socket drops and returns, lost state shows, missed data arrives with no Refresh", async ({ relayed, guard }) => {
    const seed = await api.batchAt("Created", "farm-b", "seed");
    const page = await relayed.actor("olivia");
    await expect(page.getByText("Live updates: live")).toBeVisible();
    await settle(page);
    await expect(batchesTable(page).getByRole("link", { name: seed.product })).toBeVisible();

    const end = guard.expectSocketLoss();
    relayed.relay.dropSockets();
    await expect(page.getByRole("alert").filter({ hasText: "Live updates: connection lost" })).toBeVisible();
    await expect(page.getByText(/(Last data .* ago|No data received)\. Retrying\./)).toBeVisible();

    const missed = await api.create("olivia", "farm-b", productName("missed"));
    await expect(batchesTable(page).getByRole("link", { name: missed.product })).toHaveCount(0);
    relayed.relay.restore();
    await expect(page.getByText("Live updates: live")).toBeVisible({ timeout: 40_000 });
    await expect(batchesTable(page).getByRole("link", { name: missed.product })).toBeVisible();
    end();
  });

  test("REQ-BT-032 TM-702 sign in, batches, batch, alerts and catalog send nothing for 60 s idle", async ({ actor, page }) => {
    test.setTimeout(120_000);
    await lineApi.quiet();
    const batch = await api.batchAt("Closed", "farm-a", "idle");
    const signInPage = page;
    await signInPage.goto("/signin");
    await expect(chooseUser(signInPage, "olivia")).toBeVisible();

    const list = await actor("olivia");
    const detail = await actor("olivia");
    await detail.goto(`/batches/${batch.farmId}/${batch.id}`);
    const alerts = await actor("olivia");
    await alerts.goto("/alerts");
    const catalog = await actor("olivia");
    await catalog.goto("/design-system");

    const pages = { signin: signInPage, list, detail, alerts, catalog };
    for (const open of Object.values(pages)) await settle(open);

    const sent: string[] = [];
    for (const open of Object.values(pages)) trackRequests(open, sent);
    // The row is an observation over 60 s of silence, so the window itself is the case.
    await signInPage.waitForTimeout(OBSERVE_MS);
    expect(sent).toEqual([]);
  });

  test("REQ-BT-032 TM-702 dashboard sends nothing for 60 s idle", async ({ actor }) => {
    test.setTimeout(120_000);
    await lineApi.quiet();
    const page = await actor("ada");
    await page.goto("/dashboard");
    await expect(page.getByRole("heading", { level: 1, name: "Dashboard" })).toBeVisible();
    await settle(page);
    const sent: string[] = [];
    trackRequests(page, sent);
    await page.waitForTimeout(OBSERVE_MS);
    expect(sent).toEqual([]);
  });

  test("REQ-BT-032 TM-704 line at rate 5 keeps refetches throttled to one a second", async ({ actor, line }) => {
    test.setTimeout(150_000);
    const page = await actor("ada");
    await expect(page.getByText("Live updates: live")).toBeVisible();
    await settle(page);
    const reads: number[] = [];
    page.on("request", (request) => {
      if (request.method() === "GET" && /\/api\/batches\?/.test(request.url())) reads.push(Date.now());
    });
    await line.start(5);
    await expect.poll(() => reads.length, { timeout: 40_000, message: "refetches must follow hops" }).toBeGreaterThanOrEqual(10);
    await line.stop();
    await lineApi.quiet();
    const gaps = reads.slice(1).map((at, i) => at - reads[i]!);
    // The browser reports the send a few ms either side of the timer, so 900 stands for the 1 s window.
    expect(Math.min(...gaps), `gaps ${gaps.join(" ")}`).toBeGreaterThanOrEqual(900);
  });

  test("REQ-BT-016 TM-614 viewer socket while the line runs on three farms carries farm-a only", async ({ page, line }) => {
    test.setTimeout(120_000);
    const farms = new Set<string>();
    let hops = 0;
    page.on("websocket", (socket) => {
      socket.on("framereceived", (frame) => {
        if (typeof frame.payload !== "string") return;
        for (const hop of JSON.parse(frame.payload) as { farmId: string }[]) {
          farms.add(hop.farmId);
          hops += 1;
        }
      });
    });
    await page.goto("/signin");
    await chooseUser(page, "viewer").click();
    await expect(page.getByText("Live updates: live")).toBeVisible();
    const start = hops;
    await line.start(5);
    await expect.poll(() => hops - start, { timeout: 40_000, message: "viewer must receive farm-a hops" }).toBeGreaterThan(20);
    await line.stop();
    await lineApi.quiet();
    expect([...farms]).toEqual(["farm-a"]);
    await expect(page.locator('td[data-label="Farm"]').filter({ hasNotText: /^farm-a$/ })).toHaveCount(0);
  });
});
