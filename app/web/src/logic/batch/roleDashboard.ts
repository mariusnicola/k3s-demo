import { BATCH_STATES, type Batch, type BatchState, type Me, type Summary } from "../../api/types";
import { can, nextStep, stepGate } from "./batchRules";
import { formatCount } from "../format";

export type DashboardRole = "admin" | "inspector" | "operator" | "reader";

export function roleOf(me: Me | null): DashboardRole {
  if (can(me, "ops.admin")) return "admin";
  if (can(me, "batch.inspect")) return "inspector";
  return can(me, "batch.write") ? "operator" : "reader";
}

export function farmWords(me: Me | null): string {
  const farms = me?.farms ?? [];
  return farms.includes("*") ? "all farms" : farms.join(" and ");
}

/** When the batch entered this state. The flow only moves forward, so there is at most one. */
export function stepTime(batch: Batch, state: BatchState): string | null {
  return batch.steps.find((step) => step.state === state)?.at ?? null;
}

function isToday(at: string, now: number): boolean {
  return new Date(at).toDateString() === new Date(now).toDateString();
}

export function countToday(rows: readonly Batch[], state: BatchState, now: number): number {
  return rows.filter((batch) => {
    const at = stepTime(batch, state);
    return at !== null && isToday(at, now);
  }).length;
}

/** The list route stops at its limit, so a full list can only prove a floor. */
export function countText(count: number, capped: boolean): string {
  return capped ? `At least ${formatCount(count)}` : formatCount(count);
}

export function waitingForOperator(rows: readonly Batch[], me: Me | null, now: number): Batch[] {
  return rows.filter((batch) => nextStep(batch.state).kind === "advance" && stepGate(batch, me, now) === null);
}

export function oldestFirst(rows: readonly Batch[]): Batch[] {
  return [...rows].sort((a, b) => a.createdAt.localeCompare(b.createdAt));
}

export function closedNewestFirst(rows: readonly Batch[]): Batch[] {
  return [...rows].sort((a, b) => (stepTime(b, "Closed") ?? "").localeCompare(stepTime(a, "Closed") ?? ""));
}

export function stateCounts(summary: Summary): Record<BatchState, number> {
  return Object.fromEntries(BATCH_STATES.map((state) => [state, summary.byState[state] ?? 0])) as Record<BatchState, number>;
}
