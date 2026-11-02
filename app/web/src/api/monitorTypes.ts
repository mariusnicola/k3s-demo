/** Mirrors docs/architecture/monitoring.md. */

export interface Check {
  name: string;
  healthy: boolean;
  error: string | null;
}

export interface NodeReport {
  name: string;
  role: string;
  ready: boolean;
  /** Null for the control plane: it is no Kubernetes node. */
  allocatableMemoryMi: number | null;
  cpuUsageMilli: number | null;
  memoryUsageMi: number | null;
}

export interface ChangeEntry {
  time: string;
  kind: string;
  object: string;
  old: string | null;
  new: string | null;
}

export type OperationState = "running" | "no-signal" | "done" | "failed";

export interface OperationStep {
  name: string;
  state: "pending" | "running" | "done" | "failed";
  startedAt: string | null;
  endedAt: string | null;
}

export interface ClusterOperation {
  id: string;
  name: string;
  step: string | null;
  startedAt: string;
  lastSeenAt: string;
  state: OperationState;
  endedAt: string | null;
  correlationId?: string | null;
  /** Names the job, so a new run of it is known to replace an old failure. */
  work?: string;
  steps?: OperationStep[];
  etaSeconds?: number | null;
}

export interface PodReport {
  namespace: string;
  name: string;
  node: string;
  phase: string;
  /** Missing from a monitor older than this field, which is not the same as not ready. */
  ready?: boolean;
  /** From the delete until the pod is gone. Such a pod can still read ready. */
  terminating?: boolean;
  /** A Worker that passed an event in the last minute or has work in flight now. Null or missing: unknown. Null on every other pod. */
  busy?: boolean | null;
  cpuLimitMilli: number;
  memoryLimitMi: number;
  cpuUsageMilli: number | null;
  memoryUsageMi: number | null;
}

export interface CapReport {
  node: string;
  resource: "cpuMilli" | "memoryMi";
  cap: number;
  sum: number;
  breach: boolean;
}

export interface MonitorReport {
  checkedAt: string;
  healthy: boolean;
  checks: Check[];
  nodes?: NodeReport[];
  pods?: PodReport[];
  caps?: CapReport[];
  capIssues?: string[];
  /** The pod holding the Dispatcher Lease; only that pod works. Null: released, past its lease or unreadable. Missing from an older monitor. */
  dispatcherLeader?: string | null;
  /** The store the app runs on. Null or missing: the monitor did not say. */
  store?: string | null;
  /** The most Worker pods that can run. Null or missing: unknown, never 0. */
  workers?: { max: number } | null;
}
