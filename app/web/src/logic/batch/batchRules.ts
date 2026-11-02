import { DELAYED_EVENT_MS, KNOWN_FARMS } from "../../config";
import type { Batch, BatchState, Me, Permission, Step } from "../../api/types";

export const STATE_LABELS: Record<BatchState, string> = {
  Created: "Created",
  Dose1: "Dose 1",
  Dose2: "Dose 2",
  Inspected: "Inspected",
  Shipped: "Shipped",
  Closed: "Closed",
  Rejected: "Rejected",
};

export function can(me: Me | null, permission: Permission): boolean {
  return me?.permissions.includes(permission) ?? false;
}

export function isFinal(state: BatchState): boolean {
  return state === "Closed" || state === "Rejected";
}

export type EventStatus = "idle" | "inflight" | "delayed" | "blocked";

/** Measured from the step, not sentAt: the Dispatcher rewrites sentAt on every resend, so that age never grows. */
export function pendingSince(batch: Batch): number {
  return Date.parse(batch.steps.at(-1)?.at ?? batch.createdAt);
}

export function eventStatus(batch: Batch, now: number): EventStatus {
  const pending = batch.pending;
  if (!pending) return "idle";
  if (pending.status === "dead") return "blocked";
  return now - pendingSince(batch) > DELAYED_EVENT_MS ? "delayed" : "inflight";
}

export type NextStep =
  | { kind: "advance"; to: BatchState; irreversible: boolean }
  | { kind: "inspect" }
  | { kind: "final" };

/** Dose 2 only moves through inspect; the API refuses advance to Inspected. */
export function nextStep(state: BatchState): NextStep {
  switch (state) {
    case "Created":
      return { kind: "advance", to: "Dose1", irreversible: false };
    case "Dose1":
      return { kind: "advance", to: "Dose2", irreversible: false };
    case "Dose2":
      return { kind: "inspect" };
    case "Inspected":
      return { kind: "advance", to: "Shipped", irreversible: false };
    case "Shipped":
      return { kind: "advance", to: "Closed", irreversible: true };
    default:
      return { kind: "final" };
  }
}

/** Null means allowed; a string is the reason shown beside the disabled control. */
export type Gate = string | null;

function eventGate(status: EventStatus): Gate {
  if (status === "blocked") return "Blocked. An admin must replay the failed event first.";
  if (status === "inflight" || status === "delayed") return "An event is in flight. The next step opens when its compliance record is written.";
  return null;
}

export function stepGate(batch: Batch, me: Me | null, now: number): Gate {
  const next = nextStep(batch.state);
  if (next.kind === "final") return null;
  const required = next.kind === "inspect" ? "batch.inspect" : "batch.write";
  if (!can(me, required)) {
    return next.kind === "inspect"
      ? "Inspection needs the Inspector or Admin role."
      : "Moving a batch needs the Operator or Admin role.";
  }
  return eventGate(eventStatus(batch, now));
}

export function replayGate(batch: Batch, me: Me | null, now: number): Gate {
  if (!can(me, "ops.admin")) return "Replay needs the Admin role.";
  return eventStatus(batch, now) === "blocked" ? null : "Replay is only possible while the batch is Blocked.";
}

export function failNextGate(batch: Batch, me: Me | null): Gate {
  if (!can(me, "ops.admin")) return "Failure injection needs the Admin role.";
  if (isFinal(batch.state)) return "This batch is final and has no next delivery.";
  // Replay clears the setting, so arming it here would promise a failure that never happens.
  if (batch.pending?.status === "dead") return "Blocked. Replay clears any failure set on this batch, so replay it first.";
  if (batch.failNext) return "The next delivery of this batch is already set to fail.";
  return null;
}

export type AttentionFilter = "" | "blocked" | "flagged" | "inflight";

export function matchesAttention(batch: Batch, filter: AttentionFilter, now: number): boolean {
  const status = eventStatus(batch, now);
  switch (filter) {
    case "blocked":
      return status === "blocked";
    case "flagged":
      return batch.flags.length > 0 && !isFinal(batch.state);
    case "inflight":
      return status === "inflight" || status === "delayed";
    default:
      return true;
  }
}

export function farmChoices(me: Me | null): string[] {
  if (!me) return [];
  return me.farms.includes("*") ? KNOWN_FARMS : me.farms;
}

export type ChainLink = "first" | "linked" | "broken" | "unknown";

/** Compares the two stored strings only. It proves nothing about the hashes, so a match must not read as verified. */
export function chainLink(steps: readonly Step[], index: number): ChainLink {
  const step = steps[index];
  if (!step?.hash) return "unknown";
  if (index === 0) return "first";
  const before = steps[index - 1];
  if (!before?.hash) return "unknown";
  return step.previousHash === before.hash ? "linked" : "broken";
}
