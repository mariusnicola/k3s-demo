import type { StationName } from "../../api/types";
import type { PodReport } from "../../api/monitorTypes";
import { LOAD_BALANCER_ADDRESS } from "../../config";
import { idleStores, storeLabel } from "../feed/store";
import { SERVICES, SERVICE_LABEL, podService, type PodService } from "./pods";

/** What a request, a scrape or a scale decision passes through. Cluster plumbing stays off the map. */
const SHOWN_NAMESPACES = new Set(["batch-tracker", "ingress-nginx", "prometheus", "keda", "metallb-system"]);
/** A node that is itself a container on the Docker plate. */
const NODE_CONTAINER: Record<string, string> = { "k3s-node1": "Docker container batchtracker-node1" };
export const CELL = 1.8;
export const CUBE = 1.3;
export const HEAD_W = 3;
export const HEAD_D = 2;
export const LANE_GAP = 1.5;
/** Station text is wider than the block under it. */
const STORE_GAP = 3.5;
const PER_ROW = 3;
/** At least a compact block's height, so no block stands over its neighbour. */
const COMPACT_GAP = 0.5;
export const platePad = (compact: boolean) => (compact ? 0.75 : 1);
/** Ground kept clear along the front edge for the plate name. */
const nameStrip = (plan: PlatePlan) => 1.6 + 0.8 * plan.note.split("\n").length;

export interface Lane {
  label: string;
  station: StationName | null;
  pods: PodReport[];
}

export interface PlatePlan {
  name: string;
  note: string;
  lanes: Lane[];
  /** The stores stand side by side: they have no pods to wrap. */
  sideBySide: boolean;
  /** Stands in the row below the nodes. */
  outside?: boolean;
}

function homes(names: string[], pods: PodReport[]): Record<"api" | "dispatcher" | "worker", string> {
  const home = (service: PodService): string => {
    const count = (name: string) => pods.filter((p) => plateName(p) === name && podService(p.name) === service).length;
    return names.reduce((best, name) => (count(name) > count(best) ? name : best), names[0]!);
  };
  return { api: home("api"), dispatcher: home("dispatcher"), worker: home("worker") };
}

function plateName(pod: PodReport): string {
  return pod.node || "Not placed";
}

function nodeNote(name: string, drawn: number, pods: PodReport[] | null): string {
  if (pods === null) return "Pods unknown";
  if (pods.length === 0) return "No pods reported";
  const total = pods.filter((p) => plateName(p) === name).length;
  return [`${drawn} of ${total} pods drawn`, NODE_CONTAINER[name]].filter(Boolean).join("\n");
}

export function plans(pods: PodReport[] | null, store?: string | null): PlatePlan[] {
  const shown = (pods ?? []).filter((p) => SHOWN_NAMESPACES.has(p.namespace));
  const names = [...new Set(shown.map(plateName))].sort();
  if (names.length === 0) names.push("Cluster");
  const home = homes(names, shown);
  const nodes = names.map((name): PlatePlan => {
    const here = shown.filter((p) => plateName(p) === name);
    const lanes = SERVICES.flatMap((service): Lane[] => {
      const lanePods = here.filter((p) => podService(p.name) === service);
      const station = (service === "api" || service === "dispatcher" || service === "worker") && home[service] === name ? service : null;
      return station || lanePods.length > 0 ? [{ label: SERVICE_LABEL[service], station, pods: lanePods }] : [];
    });
    const nightly: Lane[] = home.worker === name ? [{ label: "Nightly job", station: null, pods: [] }] : [];
    return { name, note: nodeNote(name, here.length, pods), lanes: [...lanes, ...nightly], sideBySide: false };
  });
  const stations: Lane[] = [{ label: storeLabel(store), station: "cosmos", pods: [] }, { label: "Service Bus", station: "bus", pods: [] }];
  const containers: Lane[] = ["Service Bus SQL", ...idleStores(store).map((s) => s.label), "Line", "Monitor"].map((label) => ({ label, station: null, pods: [] }));
  const docker: PlatePlan = { name: "Docker", note: "Containers outside the cluster", lanes: [...stations, ...containers], sideBySide: true, outside: true };
  const balancer: PlatePlan = { name: "Load balancer", note: `MetalLB holds ${LOAD_BALANCER_ADDRESS}`, lanes: [{ label: LOAD_BALANCER_ADDRESS, station: null, pods: [] }], sideBySide: true, outside: true };
  const browser: PlatePlan = { name: "Browser", note: "Outside the cluster", lanes: [{ label: "This page", station: null, pods: [] }], sideBySide: true, outside: true };
  return [...nodes, docker, balancer, browser];
}

export function laneSize(lane: Lane, perRow: number, stacked: boolean): { w: number; d: number } {
  const count = lane.pods.length;
  const columns = Math.min(perRow, Math.max(count, 1));
  const rows = Math.ceil(count / perRow);
  const span = (n: number) => n * CELL - (CELL - CUBE);
  if (stacked) return { w: Math.max(HEAD_W, count > 0 ? span(columns) : 0), d: HEAD_D + (count > 0 ? 0.5 + span(rows) : 0) };
  return { w: HEAD_W + (count > 0 ? 0.5 + span(columns) : 0), d: Math.max(HEAD_D, count > 0 ? span(rows) : 0) };
}

export interface LaneSpot {
  lane: Lane;
  x: number;
  y: number;
}

export interface Arranged {
  spots: LaneSpot[];
  w: number;
  d: number;
}

/** Containers have no pods, so all stand the same size. Rows keep the plate short, and so the links to it. */
function rows(lanes: Lane[]): Arranged {
  const [stepX, stepY] = [HEAD_W + STORE_GAP, HEAD_D + STORE_GAP];
  const spots = lanes.map((lane, i) => ({ lane, x: (i % PER_ROW) * stepX, y: Math.floor(i / PER_ROW) * stepY }));
  return { spots, w: Math.min(lanes.length, PER_ROW) * stepX - STORE_GAP, d: Math.ceil(lanes.length / PER_ROW) * stepY - STORE_GAP };
}

/** Wide, lanes run down the plate, or across it for the stores. On a phone they fill two columns, because a deep plate makes the picture wider. */
function spots(plan: PlatePlan, perRow: number, stacked: boolean): Arranged {
  const { lanes } = plan;
  const sizes = lanes.map((lane) => laneSize(lane, perRow, stacked));
  if (stacked) {
    const half = Math.ceil(lanes.length / 2);
    const first = Math.max(...sizes.slice(0, half).map((s) => s.w));
    const heights = [0, 0];
    const placed = lanes.map((lane, i) => {
      const column = i < half ? 0 : 1;
      const spot = { lane, x: column === 0 ? 0 : first + COMPACT_GAP, y: heights[column]! };
      heights[column]! += sizes[i]!.d + COMPACT_GAP;
      return spot;
    });
    const second = lanes.length > half ? COMPACT_GAP + Math.max(...sizes.slice(half).map((s) => s.w)) : 0;
    return { spots: placed, w: first + second, d: Math.max(...heights) - COMPACT_GAP };
  }
  if (plan.sideBySide) return rows(lanes);
  let along = 0;
  const placed = lanes.map((lane, i) => {
    const spot = { lane, x: 0, y: along };
    along += sizes[i]!.d + LANE_GAP;
    return spot;
  });
  return { spots: placed, w: Math.max(...sizes.map((s) => s.w)), d: along - LANE_GAP };
}

export function arrange(plan: PlatePlan, perRow: number, stacked: boolean): Arranged {
  const inner = spots(plan, perRow, stacked);
  return { spots: inner.spots, w: Math.max(inner.w, stacked ? 5 : 7) + 2 * platePad(stacked), d: inner.d + platePad(stacked) + nameStrip(plan) };
}
