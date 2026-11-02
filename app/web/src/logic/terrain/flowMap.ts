import type { BatchState } from "../../api/types";
import { PLATE_DROP, box, iso, points, rounded, type Faces, type Point } from "./geometry";

const FLOW_LINE: readonly BatchState[] = ["Created", "Dose1", "Dose2", "Inspected", "Shipped", "Closed"];
const FLOW_STEP = 3.6;
const FLOW_SIZE = 2;
const FLOW_EDGE = 0.7;
const FLOW_BRANCH = 2;
const FLOW_FLAT = 0.12;
const FLOW_BASE = 0.5;
const FLOW_RISE = 2.4;
const FLOW_MARGIN = 14;
const FLOW_TEXT_ROOM = 70;

export interface FlowBlock {
  state: BatchState;
  count: number;
  height: number;
  faces: Faces;
  top: Point;
}

export interface FlowModel {
  viewBox: string;
  plate: Faces;
  routes: string[];
  blocks: FlowBlock[];
}

/** The line along one edge, with Rejected branching off where inspection decides it. */
export function buildFlow(counts: Record<BatchState, number>): FlowModel {
  const most = Math.max(1, ...Object.values(counts));
  const cell = (state: BatchState) => (state === "Rejected" ? { x: FLOW_BRANCH * FLOW_STEP, y: FLOW_STEP } : { x: FLOW_LINE.indexOf(state) * FLOW_STEP, y: 0 });
  const middle = (state: BatchState) => [cell(state).x + FLOW_SIZE / 2, cell(state).y + FLOW_SIZE / 2, 0] as [number, number, number];
  const blocks = [...FLOW_LINE, "Rejected" as const].map((state) => {
    const count = counts[state];
    // The root keeps a small state visible beside a large one.
    const height = count > 0 ? FLOW_BASE + FLOW_RISE * Math.sqrt(count / most) : FLOW_FLAT;
    const { x, y } = cell(state);
    const faces = box({ x, y, w: FLOW_SIZE, d: FLOW_SIZE }, 0, height);
    return { state, count, height, faces, top: rounded(iso(x + FLOW_SIZE / 2, y + FLOW_SIZE / 2, height)), depth: x + y };
  });
  const ground = { x: -FLOW_EDGE, y: -FLOW_EDGE, w: (FLOW_LINE.length - 1) * FLOW_STEP + FLOW_SIZE + 2 * FLOW_EDGE, d: FLOW_STEP + FLOW_SIZE + 2 * FLOW_EDGE };
  const left = iso(ground.x, ground.y + ground.d).x - FLOW_MARGIN;
  const right = iso(ground.x + ground.w, ground.y).x + FLOW_MARGIN;
  const roof = Math.min(iso(ground.x, ground.y, PLATE_DROP).y, ...blocks.map((b) => b.top.y - FLOW_TEXT_ROOM)) - FLOW_MARGIN;
  const floor = iso(ground.x + ground.w, ground.y + ground.d, -PLATE_DROP).y + FLOW_MARGIN;
  return {
    viewBox: `${left.toFixed(1)} ${roof.toFixed(1)} ${(right - left).toFixed(1)} ${(floor - roof).toFixed(1)}`,
    plate: box(ground, -PLATE_DROP, 0),
    routes: [points(FLOW_LINE.map(middle)), points([middle("Dose2"), middle("Rejected")])],
    blocks: blocks.sort((a, b) => a.depth - b.depth),
  };
}
