import type { ClusterOperation } from "../../api/monitorTypes";
import type { OperationItem } from "../../ds";
import { formatAge, formatDateTime } from "../../logic/format";
import { etaText, hasSteps, stepViews } from "../../logic/monitor/operations";
import { seenKey } from "./seenOperations";

function elapsed(operation: ClusterOperation, now: number): string {
  return formatAge(now - Date.parse(operation.startedAt));
}

function stepFields(operation: ClusterOperation, now: number, live: boolean) {
  return hasSteps(operation) ? { name: operation.name, steps: stepViews(operation, now, live && operation.state !== "no-signal") } : {};
}

function runningItems(operations: ClusterOperation[], now: number, live: boolean): OperationItem[] {
  const result: OperationItem[] = [];
  const running = operations.filter((o) => o.state === "running");
  // With the socket lost the work may have ended long ago, so a spinner and a clock would be claims.
  const state = live ? ("running" as const) : ("no-signal" as const);
  const clock = (operation: ClusterOperation) => (live ? elapsed(operation, now) : null);
  // A checklist only means something on its own; a rollout has none, and many arrive at once.
  const stepless = running.filter((o) => !hasSteps(o));
  for (const operation of running.filter(hasSteps)) {
    result.push({ id: operation.id, state, title: operation.name, detail: null, elapsed: clock(operation), ...stepFields(operation, now, live), eta: live ? etaText(operation, now) : null });
  }
  if (stepless.length === 1) {
    const only = stepless[0]!;
    result.push({ id: only.id, state, title: only.name, detail: live || !only.step ? only.step : `Last known step: ${only.step}`, elapsed: clock(only) });
  } else if (stepless.length > 1) {
    // One deploy rolls every deployment at once, and a row for each would cover the page.
    result.push({ id: "running", state, title: `${live ? "" : "Last known: "}${stepless.length} operations running`, detail: stepless.map((o) => o.name).join(", "), elapsed: clock(stepless[0]!) });
  }
  return result;
}

function endedItems(operations: ClusterOperation[], now: number, seen: Set<string>, onSeen: ((key: string) => void) | null, canAct: boolean, live: boolean): OperationItem[] {
  const result: OperationItem[] = [];
  const steps = (operation: ClusterOperation) => stepFields(operation, now, live);
  for (const operation of operations) {
    if (seen.has(seenKey(operation))) continue;
    const seenHandler = onSeen ? { onSeen: () => onSeen(seenKey(operation)) } : {};
    const askAdmin = "Ask an admin to check it.";
    if (operation.state === "no-signal") {
      result.push({ id: operation.id, state: "no-signal", title: `No signal from ${operation.name}`, detail: `State unknown. Last heard ${formatDateTime(operation.lastSeenAt)}. ${canAct ? "Run it again to clear this." : askAdmin}`, elapsed: null, ...steps(operation), ...seenHandler });
    }
    if (operation.state === "done") {
      result.push({ id: operation.id, state: "done", title: `${operation.name} finished`, detail: null, elapsed: null, ...steps(operation), ...(hasSteps(operation) ? seenHandler : {}) });
    }
    if (operation.state === "failed") {
      const next = canAct ? "Check the pods on the Dashboard, then run it again." : askAdmin;
      const what = operation.step ? `Stopped at step ${operation.step}.` : "Stopped before it reported a step.";
      result.push({ id: operation.id, state: "failed", title: `${operation.name} failed`, detail: operation.endedAt ? `Ended ${formatDateTime(operation.endedAt)}` : null, explanation: { what, where: "Work on the cluster", next, correlationId: operation.correlationId }, logsLink: canAct, ...steps(operation), elapsed: null, ...seenHandler });
    }
  }
  return result;
}

export function items(operations: ClusterOperation[], now: number, seen: Set<string>, onSeen: ((key: string) => void) | null, canAct: boolean, live: boolean): OperationItem[] {
  return [...runningItems(operations, now, live), ...endedItems(operations, now, seen, onSeen, canAct, live)];
}
