import { BATCH_STATES } from "../../api/types";
import { USERS, batch, pending, step } from "../../mocks/fixtures";
import { chainLink, eventStatus, failNextGate, farmChoices, matchesAttention, nextStep, replayGate, stepGate } from "./batchRules";

const now = Date.parse("2026-10-30T10:01:00Z");

describe("next step", () => {
  it("REQ-BT-001 walks the flow to Closed", () => {
    expect(nextStep("Created")).toMatchObject({ to: "Dose1" });
    expect(nextStep("Dose1")).toMatchObject({ to: "Dose2" });
    expect(nextStep("Dose2")).toEqual({ kind: "inspect" });
    expect(nextStep("Inspected")).toMatchObject({ to: "Shipped" });
    expect(nextStep("Shipped")).toMatchObject({ to: "Closed", irreversible: true });
  });

  it("REQ-BT-008 no state offers advance to Inspected", () => {
    for (const state of BATCH_STATES) {
      const next = nextStep(state);
      expect(next.kind === "advance" ? next.to : null).not.toBe("Inspected");
    }
  });

  it("REQ-BT-002 Closed and Rejected offer nothing", () => {
    expect(nextStep("Closed").kind).toBe("final");
    expect(nextStep("Rejected").kind).toBe("final");
  });
});

describe("event status", () => {
  it("REQ-BT-011 dead is blocked", () => {
    expect(eventStatus(batch({ pending: pending("dead") }), now)).toBe("blocked");
  });
  it("REQ-BT-008 recent pending is in flight", () => {
    expect(eventStatus(batch({ pending: pending("sent", "2026-10-30T10:00:30Z") }), now)).toBe("inflight");
  });
  it("REQ-BT-021 a step older than the re-send window reads delayed, though each resend renews sentAt", () => {
    const old = batch({ steps: [step("Created", true, { at: "2026-10-30T09:50:00Z" })], pending: pending("sent", "2026-10-30T10:00:30Z") });
    expect(eventStatus(old, now)).toBe("delayed");
  });
  it("REQ-BT-008 no pending is idle", () => {
    expect(eventStatus(batch(), now)).toBe("idle");
  });
});

describe("gates", () => {
  it("REQ-BT-015 viewer sees why it cannot move a batch", () => {
    expect(stepGate(batch(), USERS.viewer!, now)).toBe("Moving a batch needs the Operator or Admin role.");
  });
  it("REQ-BT-015 operator cannot inspect", () => {
    expect(stepGate(batch({ state: "Dose2" }), USERS.olivia!, now)).toBe("Inspection needs the Inspector or Admin role.");
  });
  it("REQ-BT-015 inspector can inspect but cannot advance", () => {
    expect(stepGate(batch({ state: "Dose2" }), USERS.ian!, now)).toBeNull();
    expect(stepGate(batch({ state: "Dose1" }), USERS.ian!, now)).toBe("Moving a batch needs the Operator or Admin role.");
  });
  it("REQ-BT-008 advance is closed while an event is in flight", () => {
    expect(stepGate(batch({ pending: pending("sent", "2026-10-30T10:00:50Z") }), USERS.olivia!, now)).toContain("in flight");
  });
  it("REQ-BT-008 inspect is closed while an event is in flight", () => {
    expect(stepGate(batch({ state: "Dose2", pending: pending("sent", "2026-10-30T10:00:50Z") }), USERS.ian!, now)).toContain("in flight");
  });
  it("REQ-BT-008 steps are closed while blocked", () => {
    expect(stepGate(batch({ pending: pending("dead") }), USERS.olivia!, now)).toContain("Blocked");
  });
  it("REQ-BT-011 replay opens only for admin on a Blocked batch", () => {
    expect(replayGate(batch({ pending: pending("dead") }), USERS.ada!, now)).toBeNull();
    expect(replayGate(batch({ pending: pending("dead") }), USERS.olivia!, now)).toBe("Replay needs the Admin role.");
    expect(replayGate(batch(), USERS.ada!, now)).toContain("Blocked");
    expect(replayGate(batch({ pending: pending("sent", "2026-10-30T10:00:50Z") }), USERS.ada!, now)).toContain("Blocked");
  });
  it("REQ-BT-011 fail next is closed on a final batch and when already set", () => {
    expect(failNextGate(batch({ state: "Closed" }), USERS.ada!)).toContain("final");
    expect(failNextGate(batch({ failNext: true }), USERS.ada!)).toContain("already");
    expect(failNextGate(batch(), USERS.ada!)).toBeNull();
    expect(failNextGate(batch(), USERS.olivia!)).toBe("Failure injection needs the Admin role.");
  });
});

describe("attention filter and farms", () => {
  it("REQ-BT-011 filters blocked, flagged and in flight", () => {
    const blocked = batch({ pending: pending("dead") });
    const flagged = batch({ flags: [{ type: "Dose2Overdue", at: "" }] });
    expect(matchesAttention(blocked, "blocked", now)).toBe(true);
    expect(matchesAttention(flagged, "blocked", now)).toBe(false);
    expect(matchesAttention(flagged, "flagged", now)).toBe(true);
    expect(matchesAttention(blocked, "inflight", now)).toBe(false);
    expect(matchesAttention(flagged, "", now)).toBe(true);
  });

  it.each(["Closed", "Rejected"] as const)("B-039 a flag on a %s batch stays on the batch and leaves the flagged filter, as it leaves the count", (state) => {
    const finished = batch({ state, flags: [{ type: "Dose2Overdue", at: "" }] });
    expect(matchesAttention(finished, "flagged", Date.now())).toBe(false);
    expect(finished.flags).toHaveLength(1);
  });

  it("REQ-BT-016 farm choices stay with the user", () => {
    expect(farmChoices(USERS.viewer!)).toEqual(["farm-a"]);
    expect(farmChoices(USERS.ada!)).toEqual(["farm-a", "farm-b", "farm-c"]);
    expect(farmChoices(null)).toEqual([]);
  });
});

describe("chain link", () => {
  const steps = [step("Created"), step("Dose1", true, { previousHash: "hash-Created" })];
  it("REQ-BT-012 linked and first", () => {
    expect(chainLink(steps, 0)).toBe("first");
    expect(chainLink(steps, 1)).toBe("linked");
  });
  it("REQ-BT-012 mismatch shows broken", () => {
    expect(chainLink([steps[0]!, step("Dose1", true, { previousHash: "other" })], 1)).toBe("broken");
  });
  it("REQ-BT-012 unrecorded step is unknown", () => {
    expect(chainLink([steps[0]!, step("Dose1", false)], 1)).toBe("unknown");
  });
});
