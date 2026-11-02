/** Mirrors docs/architecture/data.md, api.md, events.md and monitoring.md. */

export const FLOW_STATES = ["Created", "Dose1", "Dose2", "Inspected", "Shipped", "Closed"] as const;
export type FlowState = (typeof FLOW_STATES)[number];
export type BatchState = FlowState | "Rejected";
export const BATCH_STATES: readonly BatchState[] = [...FLOW_STATES, "Rejected"];

export interface Step {
  state: BatchState;
  at: string;
  by: string;
  traceId: string;
  recordId: string;
  hash: string;
  previousHash: string;
}

export interface Flag {
  type: string;
  at: string;
}

export type PendingStatus = "pending" | "sent" | "dead";

export interface Pending {
  eventId: string;
  traceId: string;
  status: PendingStatus;
  sentAt: string | null;
  error: string | null;
  correlationId?: string | null;
}

export interface Batch {
  id: string;
  farmId: string;
  product: string;
  version: number;
  state: BatchState;
  createdAt: string;
  reason: string | null;
  failNext: boolean;
  flags: Flag[];
  steps: Step[];
  pending: Pending | null;
}

export interface ComplianceResult {
  flagged: number;
}

export type Permission = "batch.read" | "batch.write" | "batch.inspect" | "ops.admin";

export interface Me {
  user: string;
  permissions: Permission[];
  farms: string[];
}

export interface Summary {
  byState: Partial<Record<BatchState, number>>;
  pending: number;
  blocked: number;
  flagged: number;
}

export interface Alert {
  batchId: string;
  farmId: string;
  product: string;
  type: string;
  at: string | null;
  detail: string | null;
  correlationId?: string | null;
}

export const HOP_NAMES = ["api", "dispatcher", "bus", "deliver", "worker", "record", "settle"] as const;
export type HopName = (typeof HOP_NAMES)[number];
/** Where a hop goes from and to. `client` is whoever called the API. */
export type HopStation = "client" | "api" | "cosmos" | "dispatcher" | "bus" | "worker";
export type StationName = Exclude<HopStation, "client">;

export interface Hop {
  traceId: string;
  batchId: string;
  farmId: string;
  hop: HopName;
  state: string;
  at: string;
  ok: boolean;
  /** The pod that reported this hop. Older hops have none, and their dot goes to the service. */
  pod?: string;
  correlationId?: string;
  /** Sent by pods at the newer build only. The dot is placed by `to`. */
  from?: HopStation;
  to?: HopStation;
  /** Order within the trace. A resend or redelivery sorts after the first try. */
  seq?: number;
  reason?: string;
}

/** The Worker pause, for every client. */
export interface WorkerPause {
  paused: boolean;
  reason: string;
  pod: string;
  at: string;
}

export interface LineStatus {
  running: boolean;
  rate: number;
  started: number;
  closed: number;
  rejected: number;
  errors: number;
  errorsByReason: Record<string, number>;
  inFlight: number;
  /** Times the API answered 429 since the Line started. */
  throttled: number;
}

export interface LogEntry {
  at: string;
  level: string;
  reason: string;
  message: string;
  /** Null on a service entry that belongs to no batch, as are the farm, step and wait. */
  batchNumber: number | null;
  batchId: string | null;
  farm: string | null;
  step: string | null;
  waitedMs: number | null;
  httpStatus: number | null;
  exceptionType: string | null;
  exceptionMessage: string | null;
  exceptionStack?: string | null;
  traceId: string | null;
  correlationId?: string | null;
  /** What this did to the batch, in plain words. */
  impact?: string | null;
  nextAction?: string | null;
  /** Set only on an entry from the service log. */
  service?: string;
  pod?: string;
  /** Set only on a browser report. */
  reportId?: string | null;
  kind?: string | null;
  route?: string | null;
}

export interface IngressRow {
  at: string;
  status: number;
  /** nginx's own text, one group per upstream tried. Null on a row older than the field. */
  upstreamStatus: string | null;
  path: string;
}

export interface IngressLogs {
  correlationId: string;
  available: boolean;
  /** Oldest time held. Before it, no row says nothing. */
  since: string | null;
  rows: IngressRow[];
}

export interface LineLogs {
  matched: number;
  byReason: Record<string, number>;
  /** Matched entries beyond the requested limit. */
  droppedByLimit: number;
  /** Entries of every level that left memory since the line started. */
  evicted: number;
  entries: LogEntry[];
}

export interface ServiceLogs {
  matched: number;
  droppedByLimit: number;
  /** Service rows pushed out. Browser reports have their own queue, so these took none. */
  evicted: number;
  browserEvicted: number;
  entries: LogEntry[];
}

export interface LogFilters {
  /** "line" keeps the page to what the Dashboard counts. Never sent: each read already has one source. */
  source?: string;
  level: string;
  reason: string;
  farm: string;
  batch: string;
  from: string;
  to: string;
  correlationId?: string;
  /** Only the service log knows a report, so this never goes to the Line. */
  reportId?: string;
}
