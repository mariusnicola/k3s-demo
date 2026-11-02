import { HOP_NAMES, type Hop, type HopName, type StationName } from "../../api/types";
import { podService, podState, type PodService } from "../terrain/pods";
import { type CapReport, type ChangeEntry, type Check, type ClusterOperation, type MonitorReport, type NodeReport, type PodReport } from "../../api/monitorTypes";
import { parseMetricPoint, type MetricPoint } from "../monitor/metrics";
import { parseStagePoint, type StageFrame } from "../monitor/stages";
import { idleStores, storeCheck, storeKnown } from "./store";

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

/** A malformed hop loses a dot, nothing else, same as a lost hop. */
export function isHop(value: unknown): value is Hop {
  return (
    isRecord(value) &&
    typeof value.traceId === "string" &&
    typeof value.batchId === "string" &&
    HOP_NAMES.includes(value.hop as HopName) &&
    typeof value.at === "string" &&
    !Number.isNaN(Date.parse(value.at))
  );
}

function list<T>(value: unknown): T[] | undefined {
  return Array.isArray(value) ? (value as T[]) : undefined;
}

export type PerformanceFrame = { type: "metricPoint"; point: MetricPoint } | { type: "stagePoint"; point: StageFrame };

export type MonitorFrame =
  | { type: "report"; report: MonitorReport }
  | { type: "changes"; changes: ChangeEntry[] }
  | { type: "change"; change: ChangeEntry }
  | { type: "operations"; operations: ClusterOperation[] }
  | PerformanceFrame;

export function parseMonitorFrame(raw: string): MonitorFrame | null {
  try {
    const value: unknown = JSON.parse(raw);
    if (!isRecord(value)) return null;
    if (value.type === "changes" && Array.isArray(value.changes)) return { type: "changes", changes: value.changes as ChangeEntry[] };
    if (value.type === "change" && isRecord(value.change)) return { type: "change", change: value.change as unknown as ChangeEntry };
    if (value.type === "operations" && Array.isArray(value.operations)) return { type: "operations", operations: value.operations as ClusterOperation[] };
    if (value.type === "metricPoint") {
      const point = parseMetricPoint(value);
      return point ? { type: "metricPoint", point } : null;
    }
    if (value.type === "stagePoint") {
      const point = parseStagePoint(value);
      return point ? { type: "stagePoint", point } : null;
    }
    if (value.type !== undefined) return null;
  } catch {
    return null;
  }
  const report = parseReport(raw);
  return report ? { type: "report", report } : null;
}

export function parseReport(raw: string): MonitorReport | null {
  try {
    const value: unknown = JSON.parse(raw);
    if (!isRecord(value) || typeof value.checkedAt !== "string" || !Array.isArray(value.checks)) return null;
    return {
      checkedAt: value.checkedAt,
      healthy: value.healthy === true,
      checks: value.checks as Check[],
      nodes: list<NodeReport>(value.nodes),
      pods: list<PodReport>(value.pods),
      caps: list<CapReport>(value.caps),
      capIssues: list<string>(value.capIssues),
      dispatcherLeader: typeof value.dispatcherLeader === "string" ? value.dispatcherLeader : null,
      store: typeof value.store === "string" ? value.store : null,
      workers: isRecord(value.workers) && typeof value.workers.max === "number" ? { max: value.workers.max } : null,
    };
  } catch {
    return null;
  }
}

const DEDUPE_KEEP = 10_000;
/** A live tick spans 100 ms; anything further behind the newest hop in its frame is history. */
const HISTORY_GAP_MS = 2_000;

/** The hub resends its last 500 hops on every connect, so a reconnect repeats hops already seen. */
export class HopDeduper {
  private seen = new Set<string>();

  fresh(hops: readonly Hop[]): Hop[] {
    const out: Hop[] = [];
    for (const hop of hops) {
      const key = `${hop.traceId}|${hop.hop}|${hop.state}|${hop.at}|${hop.ok}|${hop.seq}`;
      if (this.seen.has(key)) continue;
      this.seen.add(key);
      out.push(hop);
    }
    for (const key of this.seen) {
      if (this.seen.size <= DEDUPE_KEEP) break;
      this.seen.delete(key);
    }
    return out;
  }
}

/** Measured inside the frame, so a skewed browser clock cannot hide live dots. */
export function liveHops(hops: readonly Hop[]): readonly Hop[] {
  const times = hops.map((h) => Date.parse(h.at));
  const newest = Math.max(...times);
  return hops.filter((_, i) => newest - times[i]! <= HISTORY_GAP_MS);
}

export function checksFailing(report: MonitorReport): Check[] {
  return report.checks.filter((c) => !c.healthy);
}

export type NodeHealth = "ok" | "failing" | "unknown";

export interface StationHealth {
  state: NodeHealth;
  text: string;
}

const CHECK_FOR_NODE: Partial<Record<StationName, string>> = {
  api: "app api ready",
  bus: "service bus",
};

/** The Service Bus check reads the `monitor` queue, so all it proves is that the emulator answers. */
const CHECK_PASSED: Partial<Record<StationName, string>> = { cosmos: "Healthy", bus: "Emulator answers" };
const PODDED: Partial<Record<StationName, PodService>> = { api: "api", dispatcher: "dispatcher", worker: "worker" };

const PHASE_TONE: Record<string, "danger" | "warning"> = { Failed: "danger", Unknown: "warning" };

export function podPhaseTone(phase: string): "danger" | "warning" | "neutral" {
  return PHASE_TONE[phase] ?? "neutral";
}

function podConcern(pod: PodReport): number {
  if (pod.phase === "Failed") return 0;
  if (pod.phase === "Unknown") return 1;
  return pod.ready === false ? 2 : 3;
}

/** The table can run to dozens of rows, so the pods that need looking at come first. */
export function podsByConcern(pods: readonly PodReport[]): PodReport[] {
  return [...pods].sort((a, b) => podConcern(a) - podConcern(b));
}

function stationHealth(name: StationName, report: MonitorReport): StationHealth {
  // A check on one store says nothing of the other.
  if (name === "cosmos" && !storeKnown(report.store)) return { state: "unknown", text: "Store not reported" };
  const wanted = name === "cosmos" ? storeCheck(report.store) : CHECK_FOR_NODE[name];
  const check = report.checks.find((c) => c.name === wanted);
  if (check && !check.healthy) return { state: "failing", text: "Failing" };
  const service = PODDED[name];
  if (!service) return check ? { state: "ok", text: CHECK_PASSED[name]! } : { state: "unknown", text: "Not reported" };
  const pods = (report.pods ?? []).filter((p) => p.namespace === "batch-tracker" && podService(p.name) === service);
  if (pods.length === 0) return { state: "unknown", text: "Pods not reported" };
  const ready = pods.filter((p) => podState(p) === "ready" || podState(p) === "not-reported").length;
  return { state: ready > 0 ? "ok" : "failing", text: `${ready} of ${pods.length} pods ready` };
}

const STATIONS: StationName[] = ["api", "cosmos", "dispatcher", "bus", "worker"];

/** The monitor has no Docker socket, so a container reads only through a check that reaches it. */
const PAD_CHECK: Record<string, [check: string, passed: string]> = {
  Line: ["ingress /line", "Answers"],
  "Service Bus SQL": ["service bus", "Implied by Service Bus"],
};

/** Words under each container with no station. The monitor proves itself by reporting. */
export function padWords(report: MonitorReport | null, fresh: boolean): Record<string, string> {
  if (!report) return {};
  const idle = idleStores(report.store).map(({ label, check }): [string, [string, string]] => [label, [check, "Answers, not in use"]]);
  const pads = [...Object.entries(PAD_CHECK), ...idle];
  if (!fresh) return Object.fromEntries([["Monitor", "Stale"], ...pads.map(([pad]) => [pad, "Stale"])]) as Record<string, string>;
  const words: Record<string, string> = { Monitor: "Reporting" };
  for (const [pad, [name, passed]] of pads) {
    const check = report.checks.find((c) => c.name === name);
    words[pad] = !check ? "Not reported" : check.healthy ? passed : "Failing";
  }
  return words;
}

export function nodeHealth(report: MonitorReport | null, fresh: boolean): Record<StationName, StationHealth> {
  const blank: StationHealth = report ? { state: "unknown", text: "Stale" } : { state: "unknown", text: "No report yet" };
  return Object.fromEntries(STATIONS.map((name) => [name, report && fresh ? stationHealth(name, report) : blank])) as Record<StationName, StationHealth>;
}
