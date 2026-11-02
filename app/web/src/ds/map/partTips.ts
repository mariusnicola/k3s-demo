import type { HopName, StationName } from "../../api/types";
import type { StationHealth } from "../../logic/feed/hops";
import { storeKnown, storeLabel } from "../../logic/feed/store";
import { infoFor, padKey, plateKey, podKey, stationKey, type PartInfo } from "../../logic/terrain/partInfo";
import { POD_STATE_TEXT } from "../../logic/terrain/pods";
import type { TerrainBlock, TerrainMap, TerrainPad, TerrainPlate, TerrainPod, TerrainStation } from "../../logic/terrain/terrain";
import { padLine, stationCount, stationHop, stationWords } from "./Blocks";

export interface Tip {
  id: string;
  /** Only a pod: its blocks show a mark and a short label, so its name says the rest. Every other part is named by its own visible words. */
  label?: string;
  info: PartInfo;
  live: string | null;
}

export interface TipInput {
  map: TerrainMap;
  tally: { counts: Record<HopName, number> | null; podCounts: Record<string, number> };
  failures: Record<HopName, number>;
  health: Record<StationName, StationHealth>;
  waiting: string | null;
  padWords: Record<string, string>;
  sender: string | null;
  leader: boolean;
  stale: boolean;
  store: string | null;
}

/** A plate that is a machine: the report says how many pods it holds. Docker and the rest say the same thing always. */
const machine = (name: string) => !["Docker", "Load balancer", "Browser"].includes(name);

function plateTip(plate: TerrainPlate): Tip {
  const info = infoFor(plateKey(plate.name), plate.name);
  return { id: `plate:${plate.name}`, info, live: machine(plate.name) ? (plate.note.split("\n")[0] ?? null) : null };
}

function stationTip(station: TerrainStation, input: TipInput): Tip {
  const { map, tally, failures, health, waiting, store } = input;
  const key = infoFor(stationKey(station.hop), station.label);
  const named = station.hop === "cosmos" && storeKnown(store) ? `The report names ${storeLabel(store)}.` : null;
  const info = station.hop === "cosmos" ? { ...key, name: station.label } : key;
  const words = stationWords({ count: stationCount(map, station, tally), failures: failures[stationHop(station.hop)], health: health[station.hop], extra: station.hop === "bus" ? waiting : null });
  return { id: `station:${station.hop}`, info, live: [named, words].filter(Boolean).join(" ") || null };
}

function podTip(pod: TerrainPod, { sender, leader, stale }: TipInput): Tip {
  const facts = [`${pod.name}: ${POD_STATE_TEXT[pod.state]}`, `on ${pod.plate}`, pod.name === sender ? (leader ? "leader" : "sending") : null].filter(Boolean).join(", ");
  return { id: `pod:${pod.name}`, label: facts, info: infoFor(podKey(pod.name), pod.name), live: stale ? `Last known: ${facts}` : facts };
}

function padTip(pad: TerrainPad, { map, tally, padWords }: TipInput): Tip {
  const key = padKey(pad.label);
  const info = key === "idle-store" ? { ...infoFor(key, pad.label), name: pad.label } : infoFor(key, pad.label);
  return { id: `pad:${pad.plate}:${pad.label}`, info, live: padLine(map, pad, tally, padWords) };
}

const blockTip = (block: TerrainBlock, input: TipInput) => (block.kind === "station" ? stationTip(block, input) : block.kind === "pod" ? podTip(block, input) : padTip(block, input));

/** One tip per drawn part, by the same order the picture draws them. */
export function partTips(input: TipInput): { plates: Tip[]; blocks: Tip[] } {
  return { plates: input.map.plates.map(plateTip), blocks: input.map.blocks.map((block) => blockTip(block, input)) };
}
