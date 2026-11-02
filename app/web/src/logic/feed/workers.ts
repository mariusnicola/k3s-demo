import type { MonitorReport, PodReport } from "../../api/monitorTypes";
import { POD_STATE_TEXT, podLabel, podService, podState, type PodService, type PodState } from "../terrain/pods";

export interface WorkerUnit {
  name: string;
  label: string;
  node: string;
  state: PodState;
  words: string;
  busy: boolean | null;
}

export interface WorkersView {
  units: WorkerUnit[];
  running: number;
  /** Of the Workers running. Unknown ones stay out, so the three add up to running. */
  busy: number;
  idle: number;
  busyUnknown: number;
  max: number | null;
  leader: string | null;
  /** Null with no leader: nobody can be a standby to no one. */
  standbys: string[] | null;
}

const appPods = (pods: PodReport[], service: PodService) => pods.filter((p) => p.namespace === "batch-tracker" && podService(p.name) === service);

/** Agrees with the map: a pod whose readiness is not reported still counts as running. */
const serving = (pod: PodReport) => podState(pod) === "ready" || podState(pod) === "not-reported";

function workerUnit(pod: PodReport): WorkerUnit {
  const state = podState(pod);
  return { name: pod.name, label: podLabel(pod.name, "worker"), node: pod.node || "Not placed", state, words: POD_STATE_TEXT[state], busy: pod.busy ?? null };
}

/** Null when the report lists no pods, which says the monitor could not read them, not that none run. */
export function workersView(report: MonitorReport): WorkersView | null {
  const pods = report.pods ?? [];
  if (pods.length === 0) return null;
  const workers = appPods(pods, "worker");
  const running = workers.filter(serving);
  const counted = (busy: boolean | null) => running.filter((p) => (p.busy ?? null) === busy).length;
  const leader = report.dispatcherLeader ?? null;
  const others = appPods(pods, "dispatcher").filter((p) => p.name !== leader && serving(p));
  return {
    units: workers.map(workerUnit),
    running: running.length,
    busy: counted(true),
    idle: counted(false),
    busyUnknown: counted(null),
    max: report.workers?.max ?? null,
    leader: leader && podLabel(leader, "dispatcher"),
    standbys: leader ? others.map((p) => podLabel(p.name, "dispatcher")) : null,
  };
}
