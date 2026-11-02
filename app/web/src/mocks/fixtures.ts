import type { Alert, Batch, Hop, LineLogs, LineStatus, LogEntry, Me, ServiceLogs, Step, Summary } from "../api/types";
import type { MonitorReport } from "../api/monitorTypes";

export const USERS: Record<string, Me> = {
  olivia: { user: "olivia", permissions: ["batch.read", "batch.write"], farms: ["farm-a", "farm-b"] },
  ian: { user: "ian", permissions: ["batch.read", "batch.inspect"], farms: ["*"] },
  ada: { user: "ada", permissions: ["batch.read", "batch.write", "batch.inspect", "ops.admin"], farms: ["*"] },
  viewer: { user: "viewer", permissions: ["batch.read"], farms: ["farm-a"] },
};

export function step(state: Step["state"], recorded = true, overrides: Partial<Step> = {}): Step {
  return {
    state,
    at: "2026-10-30T10:00:00Z",
    by: "olivia",
    traceId: `trace-${state}`,
    recordId: recorded ? `rec-${state}` : "",
    hash: recorded ? `hash-${state}` : "",
    previousHash: "",
    ...overrides,
  };
}

export function batch(overrides: Partial<Batch> = {}): Batch {
  return {
    id: "b-1",
    farmId: "farm-a",
    product: "Bovine vaccine",
    version: 1,
    state: "Dose1",
    createdAt: "2026-10-30T09:00:00Z",
    reason: null,
    failNext: false,
    flags: [],
    steps: [step("Created"), step("Dose1", true, { previousHash: "hash-Created" })],
    pending: null,
    ...overrides,
  };
}

export function pending(status: "pending" | "sent" | "dead", sentAt: string | null = null, error: string | null = null): NonNullable<Batch["pending"]> {
  return { eventId: "b-1:Dose1", traceId: "trace-1", status, sentAt, error };
}

export const SUMMARY: Summary = { byState: { Created: 1, Dose1: 2, Closed: 4, Rejected: 1 }, pending: 2, blocked: 1, flagged: 3 };

export function hop(overrides: Partial<Hop> = {}): Hop {
  return { traceId: "trace-1", batchId: "b-1", farmId: "farm-a", hop: "api", state: "Dose1", at: "2026-10-30T10:00:00.000Z", ok: true, ...overrides };
}

export const REPORT: MonitorReport = {
  checkedAt: new Date().toISOString(),
  healthy: true,
  store: "cosmos",
  checks: [
    { name: "cosmos", healthy: true, error: null },
    { name: "service bus", healthy: true, error: null },
  ],
  nodes: [
    { name: "Node1", role: "control plane", ready: true, allocatableMemoryMi: null, cpuUsageMilli: 300, memoryUsageMi: 6000 },
    { name: "k3s-node2", role: "worker", ready: true, allocatableMemoryMi: 8192, cpuUsageMilli: 1200, memoryUsageMi: 4096 },
  ],
  pods: [{ namespace: "batch-tracker", name: "api-0", node: "k3s-node1", phase: "Running", cpuLimitMilli: 500, memoryLimitMi: 512, cpuUsageMilli: 40, memoryUsageMi: 180 }],
  caps: [{ node: "k3s-node2", resource: "memoryMi", cap: 8192, sum: 4096, breach: false }],
  capIssues: [],
};

export function logEntry(overrides: Partial<LogEntry> = {}): LogEntry {
  return {
    at: "2026-10-31T10:00:00.000Z",
    level: "error",
    reason: "event-timeout",
    message: "The Line stopped waiting after 30 s for the previous event of batch 7.",
    batchNumber: 7,
    batchId: "b-1",
    farm: "farm-a",
    step: "Dose1",
    waitedMs: 3000,
    httpStatus: 503,
    exceptionType: "TaskCanceledException",
    exceptionMessage: "The request was canceled.",
    traceId: "trace-1",
    ...overrides,
  };
}

export function lineLogs(overrides: Partial<LineLogs> = {}): LineLogs {
  return { matched: 1, byReason: { "event-timeout": 1 }, droppedByLimit: 0, evicted: 0, entries: [logEntry()], ...overrides };
}

export function serviceEntry(overrides: Partial<LogEntry> = {}): LogEntry {
  return {
    at: "2026-10-31T09:00:00.000Z",
    level: "error",
    reason: "service-error",
    message: "A worker could not finish.",
    service: "worker",
    pod: "worker-7c9",
    batchNumber: null,
    batchId: null,
    farm: null,
    step: null,
    waitedMs: null,
    httpStatus: null,
    exceptionType: null,
    exceptionMessage: null,
    traceId: null,
    correlationId: "corr-live",
    ...overrides,
  };
}

export function serviceLogs(overrides: Partial<ServiceLogs> = {}): ServiceLogs {
  return { matched: 1, droppedByLimit: 0, evicted: 0, browserEvicted: 0, entries: [serviceEntry()], ...overrides };
}

export const LINE_STOPPED: LineStatus = { running: false, rate: 0, started: 0, closed: 0, rejected: 0, errors: 0, errorsByReason: {}, inFlight: 0, throttled: 0 };

export function alert(overrides: Partial<Alert> = {}): Alert {
  return { type: "Blocked", batchId: "b-1", farmId: "farm-a", product: "Bovine vaccine", detail: null, at: "2026-10-30T10:00:00Z", ...overrides };
}

/** What a valid ops.admin session gets first on the monitor socket, in this order. */
export function adminMonitorFrames(operations: unknown[] = [], changes: unknown[] = [], report: MonitorReport = REPORT) {
  return [report, { type: "changes", changes }, { type: "operations", operations }];
}

/** The same session before the monitor's first report exists: changes and operations only. */
export function adminFramesBeforeReport(operations: unknown[] = [], changes: unknown[] = []) {
  return [{ type: "changes", changes }, { type: "operations", operations }];
}

/** All anyone else ever gets on the monitor socket: no report, no changes, and nothing to say which this is. */
export function visitorMonitorFrames(operations: unknown[] = []) {
  return [{ type: "operations", operations }];
}

export function hopSnapshot(overrides: Record<string, unknown> = {}) {
  return { type: "snapshot", at: "2026-10-31T10:00:00Z", reconciledAt: "2026-10-31T10:00:00Z", hops: [], summary: SUMMARY, alerts: [], batches: [], batchesDropped: 0, capped: false, ...overrides };
}

export function lineSnapshot(overrides: Record<string, unknown> = {}) {
  return { type: "snapshot", at: "2026-10-31T10:00:00Z", status: LINE_STOPPED, entries: [], entriesDropped: 0, evicted: 0, ...overrides };
}
