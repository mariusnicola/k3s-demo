import type { Page } from "@playwright/test";
import { api } from "../support/api";
import { expect, test } from "../support/fixtures";
import { batchPath, blockedPress, bodyRows, button, countRequests, detail, expectDisabled, expectEnabled, openBatch, panel, pressWhileBlocked, recordsTable, timelineSteps } from "../support/ui";

const IN_FLIGHT = "An event is in flight. The next step opens when its compliance record is written.";
const LACKS_INSPECT = "Inspection needs the Inspector or Admin role.";
const LACKS_WRITE = "Moving a batch needs the Operator or Admin role.";
const MOVES = /\/api\/batches\/[^/]+\/[^/]+\/(advance|inspect)$/;

async function move(page: Page, to: string, next: string): Promise<void> {
  await button(page, `Move to ${to}`).click();
  await expectEnabled(button(page, next));
}

async function openRecords(page: Page): Promise<void> {
  await page.getByRole("tab", { name: "Compliance records" }).click();
}

test.describe("batch screen", () => {
  test("REQ-BT-009 TM-232 olivia advances and the record appears with no Refresh", async ({ actor }) => {
    const batch = await api.batchAt("Created");
    const page = await actor("olivia");
    await openBatch(page, batch);
    await openRecords(page);
    await expect(bodyRows(recordsTable(page))).toHaveCount(1);
    await button(page, "Move to Dose 1").click();
    await expect(bodyRows(recordsTable(page))).toHaveCount(2);
    await page.reload();
    await openRecords(page);
    await expect(bodyRows(recordsTable(page))).toHaveCount(2);
  });

  test("REQ-BT-009 TM-233 pressing Move while the event is in flight does nothing, with the reason shown", async ({ actor }) => {
    const batch = await api.batchAt("Created");
    const page = await actor("olivia");
    await openBatch(page, batch);
    const moves = countRequests(page, "POST", MOVES);
    await pressWhileBlocked(page, "Move to Dose 2");
    await button(page, "Move to Dose 1").click();
    await expect.poll(() => blockedPress(page), { message: "Move to Dose 2 never rendered disabled" }).toEqual({ reason: IN_FLIGHT });
    await expectEnabled(button(page, "Move to Dose 2"));
    expect(moves.count, "only the first move reached the API").toBe(1);
    expect((await api.read("ada", batch)).batch.state).toBe("Dose1");
  });

  test("REQ-BT-009 TM-234 walk to Closed has six records with hashes and neutral chain links", async ({ actor }) => {
    const batch = await api.batchAt("Closed");
    const page = await actor("olivia");
    await openBatch(page, batch);
    await openRecords(page);
    const rows = bodyRows(recordsTable(page));
    await expect(rows).toHaveCount(6);
    await expect(recordsTable(page).getByText("Start of chain")).toHaveCount(1);
    await expect(recordsTable(page).getByText("Links to previous")).toHaveCount(5);
    await expect(recordsTable(page).getByText("Link mismatch")).toHaveCount(0);
    await expect(recordsTable(page).getByText(/verified/i)).toHaveCount(0);
    const hashes = await rows.locator('td[data-label="Hash"] code').evaluateAll((codes) => codes.map((code) => code.getAttribute("aria-label")));
    expect(hashes).toHaveLength(6);
    for (const hash of hashes) expect(hash).toMatch(/^[0-9a-f]{64}$/);
    expect(new Set(hashes).size).toBe(6);
  });

  test("REQ-BT-009 TM-235 an event arrives over an old ETag and the next move works with no 412", async ({ actor }) => {
    const batch = await api.batchAt("Created");
    const page = await actor("olivia");
    await openBatch(page, batch);
    await expectEnabled(button(page, "Move to Dose 1"));
    await api.advance("olivia", batch, "Dose1");
    await expectEnabled(button(page, "Move to Dose 2"));
    await button(page, "Move to Dose 2").click();
    await expect(page.getByRole("status").filter({ hasText: "Moved to Dose 2." })).toBeVisible();
    expect((await api.read("ada", batch)).batch.state).toBe("Dose2");
  });

  test("REQ-BT-001 TM-545 olivia and ian walk one batch to Closed with a confirm before Close", async ({ actor }) => {
    const batch = await api.batchAt("Created");
    const olivia = await actor("olivia");
    const ian = await actor("ian");
    await openBatch(olivia, batch);
    await openBatch(ian, batch);

    await move(olivia, "Dose 1", "Move to Dose 2");
    await button(olivia, "Move to Dose 2").click();

    await expectEnabled(button(ian, "Record inspection"));
    await button(ian, "Record inspection").click();
    await button(ian, "Record pass").click();
    await expect(ian.getByRole("dialog")).toHaveCount(0);

    await expectEnabled(button(olivia, "Move to Shipped"));
    await move(olivia, "Shipped", "Move to Closed");
    await button(olivia, "Move to Closed").click();
    const confirm = olivia.getByRole("dialog", { name: "Close this batch" });
    await expect(confirm).toContainText("Closed is final.");
    await button(olivia, "Cancel").click();
    await expect(confirm).toHaveCount(0);
    expect((await api.read("ada", batch)).batch.state).toBe("Shipped");

    await button(olivia, "Move to Closed").click();
    await button(olivia, "Close batch").click();
    await expect(panel(olivia, "Next step").getByText("This batch is complete. No further step exists.")).toBeVisible();

    await olivia.reload();
    await expect(olivia.getByRole("img", { name: "Step 6 of 6" })).toBeVisible();
    await expect(panel(olivia, "Next step").getByText("Closed, final")).toBeVisible();
    await expect(panel(olivia, "Next step").getByText("This batch is complete. No further step exists.")).toBeVisible();
    await expect(detail(olivia, "Steps")).toHaveText("6");
    const steps = timelineSteps(olivia);
    await expect(steps).toHaveCount(6);
    await expect(steps.nth(0)).toContainText("By olivia");
    await expect(steps.nth(3)).toContainText("By ian");
    await expect(steps.nth(5)).toContainText("By olivia");
    await expect(steps.filter({ hasText: "Compliance record written" })).toHaveCount(6);
    await openRecords(olivia);
    await expect(bodyRows(recordsTable(olivia))).toHaveCount(6);
  });

  test("REQ-BT-003 TM-546 ian fails an inspection with a reason and the batch is Rejected and final", async ({ actor }) => {
    const batch = await api.batchAt("Dose2");
    const page = await actor("ian");
    await openBatch(page, batch);
    await button(page, "Record inspection").click();
    await page.getByRole("radio", { name: /^Fail/ }).check();
    await page.getByLabel("Reason").fill("  Vial seal broken  ");
    await button(page, "Reject batch").click();
    await expect(panel(page, "Next step").getByText("Rejected, final")).toBeVisible();
    await page.reload();
    await expect(panel(page, "Next step").getByText("Rejected, final")).toBeVisible();
    await expect(panel(page, "Next step").getByText("Reason: Vial seal broken", { exact: true })).toBeVisible();
    await expect(page.getByRole("img", { name: "Stopped at Dose 2, rejected" })).toBeVisible();
    await expect(button(page, "Record inspection")).toHaveCount(0);
    await expect(button(page, /^Move to/)).toHaveCount(0);
  });

  test("REQ-BT-003 TM-547 ian fails with no reason, refused beside the field, batch stays at Dose 2", async ({ actor }) => {
    const batch = await api.batchAt("Dose2");
    const page = await actor("ian");
    await openBatch(page, batch);
    const moves = countRequests(page, "POST", MOVES);
    await button(page, "Record inspection").click();
    await page.getByRole("radio", { name: /^Fail/ }).check();
    await button(page, "Reject batch").click();
    const dialog = page.getByRole("dialog", { name: "Record inspection" });
    await expect(dialog.getByRole("alert").filter({ hasText: "A failed inspection needs a reason." })).toBeVisible();
    await page.getByLabel("Reason").fill("   ");
    await button(page, "Reject batch").click();
    await expect(dialog.getByRole("alert").filter({ hasText: "A failed inspection needs a reason." })).toBeVisible();
    expect(moves.count).toBe(0);
    await button(page, "Cancel").click();
    await page.reload();
    await expect(page.getByRole("img", { name: "Step 3 of 6" })).toBeVisible();
    await expectEnabled(button(page, "Record inspection"));
  });

  test("REQ-BT-003 TM-548 cancel a half written rejection and reopen with an empty field", async ({ actor }) => {
    const batch = await api.batchAt("Dose2");
    const page = await actor("ian");
    await openBatch(page, batch);
    await button(page, "Record inspection").click();
    await page.getByRole("radio", { name: /^Fail/ }).check();
    await page.getByLabel("Reason").fill("half a thought");
    await button(page, "Cancel").click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await button(page, "Record inspection").click();
    await expect(page.getByRole("radio", { name: /^Pass/ })).toBeChecked();
    await page.getByRole("radio", { name: /^Fail/ }).check();
    await expect(page.getByLabel("Reason")).toHaveValue("");
  });

  test("REQ-BT-003 TM-549 inspection stays disabled with the reason while olivia's event is in flight", async ({ actor }) => {
    // A normal record lands in under a second. Fail next holds this event in flight until it dead-letters.
    const batch = await api.batchAt("Dose1");
    await api.failNext(batch);
    const page = await actor("ian");
    await openBatch(page, batch);
    await expect(page.getByText("Live updates: live")).toBeVisible();
    await pressWhileBlocked(page, "Record inspection");
    await api.advance("olivia", batch, "Dose2");
    await expect.poll(() => blockedPress(page), { message: "Record inspection never rendered disabled" }).toEqual({ reason: IN_FLIGHT });
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await api.dead(batch);
    await api.replay(batch);
    await expectEnabled(button(page, "Record inspection"));
  });

  test("REQ-BT-001 TM-550 olivia at Dose 2 is told an inspector must act and no inspect control works", async ({ actor }) => {
    const batch = await api.batchAt("Dose2");
    const page = await actor("olivia");
    await openBatch(page, batch);
    const control = button(page, "Record inspection");
    await expectDisabled(control, LACKS_INSPECT);
    await expect(page.getByText(LACKS_INSPECT)).toBeVisible();
    await control.click({ force: true });
    await expect(page.getByRole("dialog")).toHaveCount(0);
  });

  test("REQ-BT-001 TM-551 ian on a batch not at Dose 2 has New batch and Move disabled and no inspect", async ({ actor }) => {
    const batch = await api.batchAt("Created");
    const page = await actor("ian");
    await expect(button(page, "New batch")).toHaveAttribute("aria-disabled", "true");
    await openBatch(page, batch);
    await expectDisabled(button(page, "Move to Dose 1"), LACKS_WRITE);
    await expect(button(page, "Record inspection")).toHaveCount(0);
  });

  test("REQ-BT-001 TM-552 final batch reached through a stale tab reads final with no action", async ({ relayed, guard }) => {
    const batch = await api.batchAt("Shipped");
    const stale = await relayed.actor("olivia");
    await openBatch(stale, batch);
    await expect(stale.getByText("Live updates: live")).toBeVisible();
    const end = guard.expectSocketLoss();
    relayed.relay.dropSockets();
    await expect(stale.getByText("Live updates: connection lost")).toBeVisible();
    await api.advance("olivia", batch, "Closed");
    await api.settled(batch);
    await expectEnabled(button(stale, "Move to Closed"));

    await button(stale, "Read again").click();
    await expect(panel(stale, "Next step").getByText("Closed, final")).toBeVisible();
    await expect(button(stale, /^Move to/)).toHaveCount(0);
    await expect(button(stale, "Record inspection")).toHaveCount(0);
    relayed.relay.restore();
    await expect(stale.getByText("Live updates: live")).toBeVisible({ timeout: 40_000 });
    end();
  });

  test("REQ-BT-006 TM-553 two tabs, one moves, the other gets the 412 message and Reload batch recovers", async ({ actor, relayed, guard }) => {
    const batch = await api.batchAt("Dose1");
    const fresh = await actor("olivia");
    const stale = await relayed.actor("olivia");
    await openBatch(fresh, batch);
    await openBatch(stale, batch);
    await expect(stale.getByText("Live updates: live")).toBeVisible();

    const end = guard.expectSocketLoss();
    relayed.relay.dropSockets();
    await expect(stale.getByText("Live updates: connection lost")).toBeVisible();
    await button(fresh, "Move to Dose 2").click();
    await api.settled(batch);

    guard.expectResponse("POST", /\/advance$/, 412);
    await expectEnabled(button(stale, "Move to Dose 2"));
    await button(stale, "Move to Dose 2").click();
    await expect(stale.getByRole("alert").filter({ hasText: "Someone else changed this batch" })).toBeVisible();
    await expect(stale.getByText("The batch was not changed.")).toBeVisible();
    await button(stale, "Reload batch").click();
    await expect(stale.getByRole("alert").filter({ hasText: "Someone else changed this batch" })).toHaveCount(0);
    await expect(stale.getByRole("img", { name: "Step 3 of 6" })).toBeVisible();
    await expect(detail(stale, "Steps")).toHaveText("3");
    relayed.relay.restore();
    await expect(stale.getByText("Live updates: live")).toBeVisible({ timeout: 40_000 });
    end();
  });

  test("REQ-BT-006 TM-554 two users move the same batch at once, one wins, the other sees 412", async ({ actor, guard }) => {
    const batch = await api.batchAt("Dose1");
    const olivia = await actor("olivia");
    const ada = await actor("ada");
    await openBatch(olivia, batch);
    await openBatch(ada, batch);
    await expectEnabled(button(olivia, "Move to Dose 2"));
    await expectEnabled(button(ada, "Move to Dose 2"));

    guard.expectResponse("POST", /\/advance$/, 412);
    // Pressed in the page, because a live update reaching the slower actor first would disable its button.
    await Promise.all([olivia, ada].map((page) => button(page, "Move to Dose 2").evaluate((el) => (el as HTMLElement).click())));

    const refusal = (page: Page) => page.getByRole("alert").filter({ hasText: "Someone else changed this batch" });
    await expect.poll(async () => (await refusal(olivia).count()) + (await refusal(ada).count()), { message: "exactly one user sees the 412" }).toBe(1);
    const { batch: stored } = await api.settled(batch);
    expect(stored.state).toBe("Dose2");
    expect(stored.steps).toHaveLength(3);
  });

  test("REQ-BT-008 TM-555 Move right after a move stays disabled until the record arrives", async ({ actor }) => {
    const batch = await api.batchAt("Inspected");
    const page = await actor("olivia");
    await openBatch(page, batch);
    await pressWhileBlocked(page, "Move to Closed");
    await button(page, "Move to Shipped").click();
    await expect.poll(() => blockedPress(page), { message: "Move to Closed never rendered disabled" }).toEqual({ reason: IN_FLIGHT });
    await expectEnabled(button(page, "Move to Closed"));
    await expect(timelineSteps(page).filter({ hasText: "Compliance record not written yet" })).toHaveCount(0);
    await expect(timelineSteps(page)).toHaveCount(5);
  });

  test("TM-558 a move with the network off reads unknown, offers Reload batch and the batch stays on screen", async ({ actor, guard }) => {
    const batch = await api.batchAt("Created");
    const page = await actor("olivia");
    await openBatch(page, batch);
    await expectEnabled(button(page, "Move to Dose 1"));
    const end = guard.expectOutage();
    await page.context().setOffline(true);
    await button(page, "Move to Dose 1").click();
    await expect(page.getByRole("alert").filter({ hasText: "The result is unknown" }).first()).toBeVisible();
    await expect(page.getByRole("heading", { level: 1, name: batch.product })).toBeVisible();
    await page.context().setOffline(false);
    end();
    await button(page, "Reload batch").click();
    await expect(page.getByRole("alert").filter({ hasText: "The result is unknown" })).toHaveCount(0);
    await button(page, "Move to Dose 1").click();
    await expectEnabled(button(page, "Move to Dose 2"));
    expect((await api.read("ada", batch)).batch.steps).toHaveLength(2);
  });

  test("REQ-BT-036 TM-559 address with a batch id that does not exist shows 404 with a way back", async ({ actor, guard }) => {
    const page = await actor("olivia");
    const id = "00000000-0000-4000-8000-000000000000";
    guard.expectResponse("GET", new RegExp(`/api/batches/farm-a/${id}$`), 404);
    await page.goto(batchPath({ farmId: "farm-a", id }));
    await expect(page.getByRole("alert").filter({ hasText: "Not found" })).toBeVisible();
    await button(page, "Back to batches").click();
    await expect(page.getByRole("heading", { level: 1, name: "Batches" })).toBeVisible();
  });
});
