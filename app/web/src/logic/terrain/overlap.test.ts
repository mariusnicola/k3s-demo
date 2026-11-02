import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import type { PodReport } from "../../api/monitorTypes";
import type { StationName } from "../../api/types";
import { buildTerrain, padRows, stationRows, type TerrainBlock, type TerrainMap } from "./terrain";

interface Box {
  name: string;
  left: number;
  right: number;
  top: number;
  bottom: number;
  /** Blocks painted after this index stand over the text. */
  order: number;
}
type Corner = [number, number];

const pod = (name: string, node: string, namespace = "batch-tracker"): PodReport => ({ namespace, name, node, phase: "Running", ready: true, cpuLimitMilli: 100, memoryLimitMi: 64, cpuUsageMilli: 1, memoryUsageMi: 1 });
const node1 = (name: string) => pod(name, "k3s-node1");
const node2 = (name: string, namespace?: string) => pod(name, "k3s-node2", namespace);

/** The pods the live cluster runs, so the picture under test is the one people see. */
const LIVE = [
  ...["api-1-aaaaa", "api-1-bbbbb", "dispatcher-1-ccccc", "dispatcher-1-ddddd", "dispatcher-1-eeeee", "worker-1-fffff", "worker-1-ggggg", "live-1-hhhhh", "netprobe-iiiii", "web-1-jjjjj"].map(node1),
  pod("metallb-speaker-kkkkk", "k3s-node1", "metallb-system"),
  node2("ingress-nginx-controller-1-lllll", "ingress-nginx"),
  node2("metallb-controller-1-mmmmm", "metallb-system"),
  node2("metallb-speaker-nnnnn", "metallb-system"),
  node2("dispatcher-node2-1-ooooo"),
  node2("worker-node2-1-ppppp"),
  node2("netprobe-qqqqq"),
  node2("keda-operator-1-rrrrr", "keda"),
  node2("keda-metrics-1-sssss", "keda"),
  node2("keda-webhooks-1-ttttt", "keda"),
  node2("prometheus-server-1-uuuuu", "prometheus"),
];

const css = readFileSync(resolve(__dirname, "../../ds/map/map.css"), "utf8");

function fontSize(selector: string): number {
  const escaped = selector.replace(/[.[\]-]/g, "\\$&");
  const found = new RegExp(`${escaped}\\s*\\{[^}]*font-size:\\s*(\\d+)px`).exec(css);
  if (!found) throw new Error(`no font size for ${selector}`);
  return Number(found[1]);
}

const COMPACT = ".ds-diagram[data-compact] ";
/** The longest words each place can show. */
const STATION_WORDS: Record<StationName, [count: string, health: string]> = {
  api: ["12,345 accepted, 12,345 failed", "2 of 2 pods ready"],
  cosmos: ["12,345 saved, 12,345 records written, 12,345 failed", "Store not reported"],
  dispatcher: ["12,345 marked sent, 12,345 failed", "3 of 3 pods ready"],
  bus: ["12,345 sent to queue, 12,345 delivered, 12,345 failed", "Emulator answers"],
  worker: ["12,345 read, 12,345 failed", "2 of 2 pods ready"],
};
const WAITING = "12,345 waiting, oldest seen 300 s";
const PAD_WORDS: Record<string, string> = { "Service Bus SQL": "Implied by Service Bus", "Cosmos DB": "Answers, not in use", MongoDB: "Answers, not in use", Line: "Not reported", Monitor: "Reporting", Dispatcher: "12,345 marked sent", Worker: "12,345 read" };

/** A letter averages this share of its size, a bold one wider. */
function textBox(name: string, words: string, x: number, baseline: number, size: number, order: number, bold = false): Box {
  const half = (words.length * size * (bold ? 0.6 : 0.55)) / 2;
  return { name, left: x - half, right: x + half, top: baseline - 0.8 * size, bottom: baseline + 0.2 * size, order };
}

function labels(map: TerrainMap, compact: boolean): Box[] {
  const title = fontSize(`${compact ? COMPACT : ""}.ds-map-title`);
  const small = fontSize(".ds-map-count");
  const pad = fontSize(`${compact ? COMPACT : ""}.ds-map-pad-label`);
  return map.blocks.flatMap((block, i): Box[] => {
    const { x } = block.top;
    if (block.kind === "pad") {
      const rows = padRows(block, compact);
      const words = compact ? undefined : PAD_WORDS[block.label];
      return [textBox(block.label, block.label, rows.x, rows.label, pad, i), ...(words ? [textBox(`${block.label} words`, words, rows.x, rows.words, pad, i)] : [])];
    }
    if (block.kind !== "station") return [];
    const rows = stationRows(block.top, compact);
    const head = textBox(block.label, block.label, x, rows.title, title, i, true);
    if (compact) return [head];
    const [count, health] = STATION_WORDS[block.hop];
    return [head, textBox(`${block.label} count`, count, x, rows.count, small, i), textBox(`${block.label} health`, health, x, rows.health, small, i), ...(block.hop === "bus" ? [textBox(`${block.label} waiting`, WAITING, x, rows.extra, small, i)] : [])];
  });
}

function corners(block: TerrainBlock): Corner[] {
  return [block.faces.top, block.faces.left, block.faces.right].flatMap((face) => face.split(" ").map((c) => c.split(",").map(Number) as Corner));
}

function hull(points: Corner[]): Corner[] {
  const sorted = [...points].sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  const turn = (o: Corner, a: Corner, b: Corner) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
  const half = (list: Corner[]) =>
    list.reduce<Corner[]>((kept, point) => {
      while (kept.length > 1 && turn(kept[kept.length - 2]!, kept[kept.length - 1]!, point) <= 0) kept.pop();
      return [...kept, point];
    }, []);
  return [...half(sorted).slice(0, -1), ...half([...sorted].reverse()).slice(0, -1)];
}

/** Two convex shapes are apart when one of their edges has them on opposite sides. */
function shapesCross(a: Corner[], b: Corner[]): boolean {
  const edges = [a, b].flatMap((shape) => shape.map((p, i): Corner => { const q = shape[(i + 1) % shape.length]!; return [q[1] - p[1], p[0] - q[0]]; }));
  return edges.every(([nx, ny]) => {
    const along = (shape: Corner[]) => shape.map(([x, y]) => x * nx + y * ny);
    return Math.min(...along(a)) < Math.max(...along(b)) - 0.01 && Math.min(...along(b)) < Math.max(...along(a)) - 0.01;
  });
}

const boxShape = (b: Box): Corner[] => [[b.left, b.top], [b.right, b.top], [b.right, b.bottom], [b.left, b.bottom]];
const blockName = (block: TerrainBlock) => (block.kind === "pod" ? block.name : block.label);

function crossingBlocks(map: TerrainMap): string[] {
  const shapes = map.blocks.map((block) => ({ name: blockName(block), shape: hull(corners(block)) }));
  return shapes.flatMap((a, i) => shapes.slice(i + 1).filter((b) => shapesCross(a.shape, b.shape)).map((b) => `${a.name} on ${b.name}`));
}

function crossingLabels(boxes: Box[]): string[] {
  return boxes.flatMap((a, i) => boxes.slice(i + 1).filter((b) => a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom).map((b) => `${a.name} on ${b.name}`));
}

function hiddenLabels(map: TerrainMap, boxes: Box[]): string[] {
  return boxes.flatMap((label) => map.blocks.slice(label.order + 1).filter((block) => shapesCross(boxShape(label), hull(corners(block)))).map((block) => `${label.name} under ${blockName(block)}`));
}

describe("the load balancer", () => {
  const screenYs = (map: TerrainMap, plate: string) => {
    const { faces } = map.plates.find((p) => p.name === plate)!;
    return [faces.top, faces.left, faces.right].flatMap((face) => face.split(" ").map((c) => Number(c.split(",")[1])));
  };

  it.each([["phone", true], ["desktop", false]] as const)("REQ-BT-028 stands above every plate that holds Worker pods on a %s", (_, compact) => {
    const map = buildTerrain(LIVE, compact, "mongo");
    const nodes = map.plates.filter((p) => p.name.startsWith("k3s-"));
    expect(nodes).toHaveLength(2);
    for (const node of nodes) expect(Math.max(...screenYs(map, "Load balancer"))).toBeLessThan(Math.min(...screenYs(map, node.name)));
  });
});

describe("the map at phone width", () => {
  const map = buildTerrain(LIVE, true, "mongo");

  it("REQ-BT-028 no block stands over another", () => {
    expect(crossingBlocks(map)).toEqual([]);
  });

  it("REQ-BT-028 no block hides a label", () => {
    expect(hiddenLabels(map, labels(map, true))).toEqual([]);
  });

  it("REQ-BT-028 no label is drawn over another", () => {
    expect(crossingLabels(labels(map, true))).toEqual([]);
  });

  it("REQ-BT-028 the check finds blocks that stand over each other", () => {
    const [first, second] = map.blocks.filter((b) => b.kind === "station");
    expect(crossingBlocks({ ...map, blocks: [first!, { ...second!, faces: first!.faces }] })).toHaveLength(1);
  });
});

describe("the map on a desktop", () => {
  const map = buildTerrain(LIVE, false, "mongo");

  it("REQ-BT-028 no block hides a store or service label, with the longest words", () => {
    expect(hiddenLabels(map, labels(map, false))).toEqual([]);
  });

  it("REQ-BT-028 no label is drawn over another, with the longest words", () => {
    expect(crossingLabels(labels(map, false))).toEqual([]);
  });
});
