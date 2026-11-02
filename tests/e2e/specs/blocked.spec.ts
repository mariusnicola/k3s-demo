import { api } from "../support/api";
import { expect, test } from "../support/fixtures";
import { bodyRows, button, batchPath, expectDisabled, expectEnabled, openBatch, panel, timelineSteps } from "../support/ui";

const BLOCKED = "Blocked: the compliance event failed";
const REPLAY_REFUSED = "Replay needs the Admin role.";
const STEP_BLOCKED = "Blocked. An admin must replay the failed event first.";

test.describe("dead letters and alerts", () => {
  test("REQ-BT-011 TM-253 ada sets fail next, advances, the batch blocks, replay opens the next step", async ({ actor }) => {
    const batch = await api.batchAt("Created");
    const page = await actor("ada");
    await openBatch(page, batch);

    await panel(page, "Admin").getByRole("button", { name: "Fail next delivery" }).click();
    await page.getByRole("dialog", { name: "Fail the next delivery" }).getByRole("button", { name: "Fail next delivery" }).click();
    await expect(page.getByText("Next delivery set to fail")).toBeVisible();

    await button(page, "Move to Dose 1").click();
    await expect(page.getByRole("alert").filter({ hasText: BLOCKED })).toBeVisible();
    await expectDisabled(button(page, "Move to Dose 2"), STEP_BLOCKED);

    await button(page, "Replay event").click();
    await expect(page.getByRole("alert").filter({ hasText: BLOCKED })).toHaveCount(0);
    await expectEnabled(button(page, "Move to Dose 2"));
    await expect(timelineSteps(page).filter({ hasText: "Compliance record written" })).toHaveCount(2);

    await page.reload();
    await expect(timelineSteps(page).filter({ hasText: "Compliance record written" })).toHaveCount(2);
    await expectEnabled(button(page, "Move to Dose 2"));
  });

  test("REQ-BT-011 TM-254 olivia on a Blocked batch sees the banner, Replay and Move disabled", async ({ actor, blocked }) => {
    const batch = await blocked();
    const page = await actor("olivia");
    await openBatch(page, batch);
    await expect(page.getByRole("alert").filter({ hasText: BLOCKED })).toBeVisible();
    await expectDisabled(button(page, "Replay event"), REPLAY_REFUSED);
    await expectDisabled(button(page, "Move to Dose 2"), STEP_BLOCKED);
  });

  test("REQ-BT-011 TM-696 ian on a Blocked batch sees the banner and Replay disabled with the reason", async ({ actor, blocked }) => {
    const batch = await blocked();
    const page = await actor("ian");
    await openBatch(page, batch);
    await expect(page.getByRole("alert").filter({ hasText: BLOCKED })).toBeVisible();
    await expectDisabled(button(page, "Replay event"), REPLAY_REFUSED);
  });

  test("REQ-BT-011 TM-255 a Blocked batch shows in Alerts with its error and link, and leaves after replay", async ({ actor, blocked }) => {
    const batch = await blocked();
    const page = await actor("ada");
    await page.goto("/alerts");
    const row = bodyRows(page.getByRole("table", { name: "Open alerts" })).filter({ hasText: batch.product });
    await expect(row.locator('td[data-label="Alert"]')).toHaveText("Blocked");
    const error = (await api.read("ada", batch)).batch.pending?.error;
    expect(error, "the dead event carries an error").toBeTruthy();
    await button(page, `Show details of Blocked on ${batch.product}`).click();
    await expect(page.locator(".ds-table-detail")).toContainText(error!);

    await row.getByRole("link", { name: batch.product }).click();
    await expect(page).toHaveURL(new RegExp(`${batchPath(batch)}$`));
    await button(page, "Replay event").click();
    await expect(page.getByRole("alert").filter({ hasText: BLOCKED })).toHaveCount(0);

    await page.getByRole("link", { name: "Alerts" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Alerts" })).toBeVisible();
    await expect(page.getByRole("link", { name: batch.product })).toHaveCount(0);
    await page.reload();
    await expect(page.getByRole("heading", { level: 1, name: "Alerts" })).toBeVisible();
    await expect(page.getByRole("link", { name: batch.product })).toHaveCount(0);
  });

  test("REQ-BT-011 TM-256 blocked on a final batch reads Closed with the record unwritten, replay writes it", async ({ actor, blocked }) => {
    const batch = await blocked("Shipped");
    const page = await actor("ada");
    await openBatch(page, batch);
    await expect(panel(page, "Next step").getByText("Closed, final")).toBeVisible();
    await expect(panel(page, "Next step").getByText("Its last compliance record is not written yet.")).toBeVisible();
    await expect(page.getByRole("alert").filter({ hasText: "The record stays unwritten until it is replayed." })).toBeVisible();
    await expect(timelineSteps(page).filter({ hasText: "Compliance record not written yet" })).toHaveCount(1);

    await button(page, "Replay event").click();
    await expect(panel(page, "Next step").getByText("This batch is complete. No further step exists.")).toBeVisible();
    await expect(timelineSteps(page).filter({ hasText: "Compliance record written" })).toHaveCount(6);
    await page.reload();
    await expect(panel(page, "Next step").getByText("This batch is complete. No further step exists.")).toBeVisible();
  });

  test("REQ-BT-013 TM-566 nearest: ada sees Blocked rows on every farm, each with a link", async ({ actor, blocked }) => {
    const rows = [await blocked("Created", "farm-a"), await blocked("Created", "farm-b")];
    const page = await actor("ada");
    await page.goto("/alerts");
    for (const batch of rows) {
      const row = bodyRows(page.getByRole("table", { name: "Open alerts" })).filter({ hasText: batch.product });
      await expect(row.locator('td[data-label="Farm"]')).toHaveText(batch.farmId);
      await expect(row.getByRole("link", { name: batch.product })).toHaveAttribute("href", batchPath(batch));
    }
  });

  test("REQ-BT-016 TM-567 viewer sees alerts of its own farm only", async ({ actor, blocked }) => {
    const own = await blocked("Created", "farm-a");
    const other = await blocked("Created", "farm-b");
    const page = await actor("viewer");
    await page.goto("/alerts");
    await expect(page.getByRole("link", { name: own.product })).toBeVisible();
    await expect(page.getByRole("link", { name: other.product })).toHaveCount(0);
    await expect(page.locator('td[data-label="Farm"]').filter({ hasNotText: /^farm-a$/ })).toHaveCount(0);
  });

  test("REQ-BT-036 TM-568 no alerts shows the empty state", async ({ actor }) => {
    // Viewer sees farm-a only. This suite never parks a batch at Dose 1 there, so nothing of its own can flag.
    for (const alert of await api.alerts("viewer")) {
      if (alert.type === "Blocked" && alert.product.startsWith("e2e ")) await api.replay({ farmId: alert.farmId, id: alert.batchId });
    }
    await expect.poll(() => api.alerts("viewer"), { message: "viewer still has alerts this suite did not make" }).toEqual([]);
    const page = await actor("viewer");
    await page.goto("/alerts");
    await expect(page.getByText("No open alerts")).toBeVisible();
    await expect(page.getByRole("table", { name: "Open alerts" })).toHaveCount(0);
  });

  test("REQ-BT-013 TM-570 link on an alert for a batch now replayed opens the batch with no stale error", async ({ relayed, guard, blocked }) => {
    const batch = await blocked();
    const page = await relayed.actor("ada");
    await page.goto("/alerts");
    const link = page.getByRole("link", { name: batch.product });
    await expect(link).toBeVisible();
    await expect(page.getByText("Live updates: live")).toBeVisible();
    const end = guard.expectSocketLoss();
    relayed.relay.dropSockets();
    await expect(page.getByText("Live updates: connection lost")).toBeVisible();

    await api.replay(batch);
    await api.settled(batch);
    await expect(link).toBeVisible();
    await link.click();
    await expect(page.getByRole("heading", { level: 1, name: batch.product })).toBeVisible();
    await expect(page.getByRole("alert").filter({ hasText: BLOCKED })).toHaveCount(0);
    await expect(button(page, "Replay event")).toHaveCount(0);
    await expectEnabled(button(page, "Move to Dose 2"));
    relayed.relay.restore();
    await expect(page.getByText("Live updates: live")).toBeVisible({ timeout: 40_000 });
    end();
  });
});
