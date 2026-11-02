import type { ClusterOperation, OperationStep } from "../../api/monitorTypes";
import { formatAge } from "../format";

export type StepMark = OperationStep["state"] | "unknown";

export interface StepView {
  name: string;
  mark: StepMark;
  note: string;
}

/** The monitor only moves the estimate when a step ends, so that moment is what it counts from. */
function estimatedAt(operation: ClusterOperation): number {
  const ended = (operation.steps ?? []).flatMap((step) => (step.endedAt ? [Date.parse(step.endedAt)] : []));
  return Math.max(Date.parse(operation.startedAt), ...ended);
}

/** Rounded up: a promise of one minute that takes two reads worse than the reverse. Zero is steps with no earlier run, so no estimate. */
export function etaText(operation: ClusterOperation, now: number): string {
  const { etaSeconds } = operation;
  if (!etaSeconds) return "No estimate yet";
  const left = etaSeconds - (now - estimatedAt(operation)) / 1000;
  return left <= 0 ? "Almost done" : `About ${Math.ceil(left / 60)} min left`;
}

function runTime(step: OperationStep, until: number): string | null {
  return step.startedAt ? formatAge(until - Date.parse(step.startedAt)) : null;
}

function viewOf(step: OperationStep, now: number, live: boolean): StepView {
  const { name } = step;
  if (step.state === "done") return { name, mark: "done", note: "Done" };
  if (step.state === "pending") return { name, mark: "pending", note: "Not started" };
  if (step.state === "failed") {
    const time = runTime(step, step.endedAt ? Date.parse(step.endedAt) : now);
    return { name, mark: "failed", note: time ? `Failed after ${time}` : "Failed" };
  }
  // With no signal the step may have ended long ago, so a clock here would be a claim.
  if (!live) return { name, mark: "unknown", note: "State unknown" };
  const time = runTime(step, now);
  return { name, mark: "running", note: time ? `Running, ${time}` : "Running" };
}

export function stepViews(operation: ClusterOperation, now: number, live: boolean): StepView[] {
  return (operation.steps ?? []).map((step) => viewOf(step, now, live));
}

export function hasSteps(operation: ClusterOperation): boolean {
  return (operation.steps?.length ?? 0) > 0;
}

function finished(step: OperationStep): boolean {
  return step.state === "done" || step.state === "failed";
}

/**
 * What to read out since the last frame. `known` remembers each step that was read, so a repeated
 * frame or a reconnect stays quiet, and steps finished before the page opened are never read.
 */
export function newTicks(operations: ClusterOperation[], known: Map<string, Set<number>>): string | null {
  const lines: string[] = [];
  for (const operation of operations) {
    const steps = operation.steps ?? [];
    const read = known.get(operation.id);
    if (!read) {
      known.set(operation.id, new Set(steps.flatMap((step, index) => (finished(step) ? [index] : []))));
      continue;
    }
    steps.forEach((step, index) => {
      if (!finished(step) || read.has(index)) return;
      read.add(index);
      lines.push(`${step.name} ${step.state === "failed" ? "failed" : "finished"}.`);
    });
  }
  return lines.length > 0 ? lines.join(" ") : null;
}
