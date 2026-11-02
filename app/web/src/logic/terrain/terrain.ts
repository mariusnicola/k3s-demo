import type { StationName } from "../../api/types";
import { type PodReport } from "../../api/monitorTypes";
import { PLATE_DROP, box, iso, rounded, type Faces, type Ground, type Point } from "./geometry";
import { buildLinks, type TerrainLink } from "./links";
import { CELL, CUBE, HEAD_D, HEAD_W, arrange, platePad, plans, type Arranged, type Lane, type LaneSpot, type PlatePlan } from "./plates";
import { POD_HEIGHT, POD_PROBLEM, SERVICES, SERVICE_LABEL, countNote, podLabel, podService, podState, type PodService, type PodState } from "./pods";

const PLATE_GAP = 2.5;
const MARGIN = 28;
const MARGIN_COMPACT = 10;
const PODS_PER_ROW = 5;
const PODS_PER_ROW_COMPACT = 3;

/** Kept low so a block never hides the lane behind it, and one height so the words of two lanes never meet. */
const STATION_HEIGHT = 1;
/** A phone packs lanes tight, so a block stands no taller than the gap to the next. */
const COMPACT_RISE = 0.45;

/** Baselines of the words on a station. Compact text is larger, because the whole picture is scaled down to fit a phone. */
export function stationRows(top: Point, compact: boolean) {
  const line = compact ? 24 : 20;
  return { title: top.y - line + 3, count: top.y + 3, health: top.y + line + 3, extra: top.y + 2 * line + 3 };
}

/** Text is wider than the pad, so it leans away from the pods beside it, and on a phone clear of those in front. */
export function padRows({ top, pods }: Pick<TerrainPad, "top" | "pods">, compact: boolean) {
  return { x: top.x - (compact || pods.length === 0 ? 0 : 26), label: top.y + (compact ? -8 : 4), words: top.y + 24 };
}

interface BlockBase {
  ground: Ground;
  faces: Faces;
  /** Screen point at the middle of the top face, where text sits. */
  top: Point;
  anchor: Point;
  plate: string;
}

export interface TerrainStation extends BlockBase {
  kind: "station";
  hop: StationName;
  label: string;
  /** Pods in this lane, so a number on one plate covers only that plate. */
  pods: string[];
}

export interface TerrainPod extends BlockBase {
  kind: "pod";
  name: string;
  label: string;
  service: PodService;
  state: PodState;
  height: number;
}

export interface TerrainPad extends BlockBase {
  kind: "pad";
  label: string;
  /** Set on a lane of pods with no station, on a node that is not that service's home. */
  pods: string[];
}

export type TerrainBlock = TerrainStation | TerrainPod | TerrainPad;

export interface TerrainPlate {
  name: string;
  note: string;
  faces: Faces;
  /** Point the plate name is painted from, along its front edge. */
  label: Point;
}

export interface TerrainMap {
  viewBox: { x: number; y: number; width: number; height: number };
  plates: TerrainPlate[];
  blocks: TerrainBlock[];
  stations: Record<StationName, TerrainStation>;
  pods: TerrainPod[];
  pads: TerrainPad[];
  links: TerrainLink[];
}

interface Arrangement {
  perRow: number;
  compact: boolean;
}

type Placed = Pick<TerrainMap, "plates" | "blocks" | "stations">;

function block(ground: Ground, height: number, plate: string) {
  const { x, y, w, d } = ground;
  return { ground, faces: box(ground, 0, height), top: rounded(iso(x + w / 2, y + d / 2, height)), anchor: rounded(iso(x + w, y + d, 0)), plate };
}

function placeHead(lane: Lane, head: Ground, plate: string, compact: boolean, placed: Placed): void {
  if (lane.station) {
    const station: TerrainStation = { kind: "station", hop: lane.station, label: lane.label, pods: lane.pods.map((p) => p.name), ...block(head, compact ? COMPACT_RISE : STATION_HEIGHT, plate) };
    placed.stations[lane.station] = station;
    placed.blocks.push(station);
  } else {
    placed.blocks.push({ kind: "pad", label: lane.label, pods: lane.pods.map((p) => p.name), ...block(head, 0.12, plate) });
  }
}

function placePods(lane: Lane, laneStart: Point, plate: string, { perRow, compact }: Arrangement, placed: Placed): void {
  lane.pods.forEach((pod, i) => {
    const column = i % perRow;
    const row = Math.floor(i / perRow);
    const startX = compact ? laneStart.x : laneStart.x + HEAD_W + 0.5;
    const startY = compact ? laneStart.y + HEAD_D + 0.5 : laneStart.y;
    const service = podService(pod.name);
    const state = podState(pod);
    const height = compact ? (POD_HEIGHT[state] * COMPACT_RISE) / POD_HEIGHT.ready : POD_HEIGHT[state];
    const ground: Ground = { x: startX + column * CELL, y: startY + row * CELL, w: CUBE, d: CUBE };
    placed.blocks.push({ kind: "pod", name: pod.name, label: podLabel(pod.name, service), service, state, height, ...block(ground, height, plate) });
  });
}

function placeLanes(plan: PlatePlan, spots: LaneSpot[], origin: Point, arrangement: Arrangement, placed: Placed): void {
  for (const { lane, x, y } of spots) {
    const pad = platePad(arrangement.compact);
    const start = { x: origin.x + pad + x, y: origin.y + pad + y };
    placeHead(lane, { x: start.x, y: start.y, w: HEAD_W, d: HEAD_D }, plan.name, arrangement.compact, placed);
    placePods(lane, start, plan.name, arrangement, placed);
  }
}

function placePlates(sized: ({ plan: PlatePlan } & Arranged)[], arrangement: Arrangement): Placed {
  const { compact } = arrangement;
  const deepest = Math.max(...sized.filter((n) => !n.plan.outside).map((n) => n.d));
  const placed: Placed = { plates: [], blocks: [], stations: {} as Record<StationName, TerrainStation> };
  let along = 0;
  let below = 0;
  let diagonal = 0;
  sized.forEach(({ plan, spots, w, d }) => {
    const x = compact ? diagonal : plan.outside ? below : along;
    const y = compact ? diagonal : plan.outside ? deepest + PLATE_GAP : 0;
    if (plan.outside) below += w + PLATE_GAP;
    else along += w + PLATE_GAP;
    // Along the diagonal a plate sits straight below the last, so the picture stays narrow.
    diagonal += (w + d) / 2 + 1.2;
    placed.plates.push({ name: plan.name, note: plan.note, faces: box({ x, y, w, d }, -PLATE_DROP, 0), label: rounded(iso(x + 0.5, y + d - 0.5, 0)) });
    placeLanes(plan, spots, { x, y }, arrangement, placed);
  });
  return placed;
}

function viewBoxAround({ plates, blocks }: Placed, links: TerrainLink[], margin: number): TerrainMap["viewBox"] {
  const corners = [...plates.flatMap((p) => [p.faces.top, p.faces.left, p.faces.right]), ...blocks.map((b) => b.faces.top)].flatMap((face) => face.split(" ").map((c) => c.split(",").map(Number) as [number, number]));
  const turns = links.flatMap((l) => l.points);
  const xs = [...corners.map((p) => p[0]), ...turns.map((p) => p.x)];
  const ys = [...corners.map((p) => p[1]), ...turns.map((p) => p.y)];
  const minX = Math.min(...xs) - margin;
  const minY = Math.min(...ys) - margin;
  return { x: Math.round(minX), y: Math.round(minY), width: Math.ceil(Math.max(...xs) + margin - minX), height: Math.ceil(Math.max(...ys) + margin - minY) };
}

export function buildTerrain(pods: PodReport[] | null, compact: boolean, store?: string | null): TerrainMap {
  const perRow = compact ? PODS_PER_ROW_COMPACT : PODS_PER_ROW;
  const sized = plans(pods, store).map((plan) => ({ plan, ...arrange(plan, perRow, compact) }));
  const placed = placePlates(sized, { perRow, compact });
  const { plates, blocks, stations } = placed;
  blocks.sort((a, b) => a.ground.x + a.ground.y - (b.ground.x + b.ground.y));
  const pods2 = blocks.filter((b): b is TerrainPod => b.kind === "pod");
  const pads = blocks.filter((b): b is TerrainPad => b.kind === "pad");
  const links = buildLinks({ stations, pods: pods2, pads }, !compact);
  return {
    viewBox: viewBoxAround(placed, links, compact ? MARGIN_COMPACT : MARGIN),
    plates,
    blocks,
    stations,
    pods: pods2,
    pads,
    links,
  };
}

/** Words for what the picture shows, because a screen reader gets none of its geometry. */
export function podSummary(map: TerrainMap): string {
  const nodes = map.plates.flatMap((plate) => {
    const here = map.pods.filter((p) => p.plate === plate.name);
    if (here.length === 0) return [];
    const parts = SERVICES.flatMap((service) => {
      const count = here.filter((p) => p.service === service).length;
      return count > 0 ? [`${SERVICE_LABEL[service]} ${countNote(count)}`] : [];
    });
    return [`${plate.name}: ${parts.join(", ")}.`];
  });
  const problems = map.pods.flatMap((p) => (POD_PROBLEM[p.state] ? [`${p.name} ${POD_PROBLEM[p.state]}.`] : []));
  return [...nodes, ...problems].join(" ");
}
