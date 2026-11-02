import { useMemo } from "react";
import { BATCH_STATES, type BatchState, type HopName, type StationName } from "../../api/types";
import { STATE_LABELS } from "../../logic/batch/batchRules";
import type { StationHealth } from "../../logic/feed/hops";
import { formatCount } from "../../logic/format";
import type { PodState } from "../../logic/terrain/pods";
import { buildFlow } from "../../logic/terrain/flowMap";
import { padRows, stationRows, type TerrainMap, type TerrainPad, type TerrainPlate, type TerrainPod, type TerrainStation } from "../../logic/terrain/terrain";
import type { Faces, Point } from "../../logic/terrain/geometry";

export const STATION_LABELS: Record<HopName, string> = {
  api: "API",
  dispatcher: "Dispatcher",
  bus: "Service Bus",
  deliver: "Delivered",
  worker: "Worker",
  record: "Record written",
  settle: "Settled",
};

/** What each number on the map counts. */
const COUNT_WORDS: Record<StationName, (n: number) => string> = {
  api: (n) => `${formatCount(n)} accepted`,
  cosmos: (n) => `${formatCount(n)} saved`,
  dispatcher: (n) => `${formatCount(n)} marked sent`,
  bus: (n) => `${formatCount(n)} sent to queue`,
  worker: (n) => `${formatCount(n)} read`,
};

interface MapCounts {
  counts: Record<HopName, number> | null;
  podCounts: Record<string, number>;
}

/** The store reports nothing of its own: the API's one hop says a step was accepted and saved. */
export const stationHop = (station: StationName): HopName => (station === "cosmos" ? "api" : station);

/** A hop from a pod that names none has no node, so the home lane holds it. */
function laneCount(hop: StationName, lanePods: string[], allPods: string[], home: boolean, { counts, podCounts }: MapCounts): number {
  const sum = (names: string[]) => names.reduce((total, name) => total + (podCounts[name] ?? 0), 0);
  if (!counts) return 0;
  return sum(lanePods) + (home ? counts[stationHop(hop)] - sum(allPods) : 0);
}

/** Colour never carries a pod state alone: height and this mark do it too. */
const POD_MARK: Partial<Record<PodState, string>> = {
  "not-ready": "!",
  pending: "...",
  failed: "\u00d7",
  unknown: "?",
  terminating: "-",
};

export function place(point: Point): string {
  return `${point.x.toFixed(1)} ${point.y.toFixed(1)}`;
}

function Solid({ faces }: { faces: Faces }) {
  return (
    <>
      <polygon points={faces.left} className="ds-map-left" />
      <polygon points={faces.right} className="ds-map-right" />
      <polygon points={faces.top} className="ds-map-top" />
    </>
  );
}

function Plate({ plate }: { plate: TerrainPlate }) {
  // Lies the name flat on the plate, along its front edge.
  const flat = `matrix(0.866 0.5 -0.866 0.5 ${plate.label.x} ${plate.label.y})`;
  return (
    <g className="ds-map-plate" data-plate-name={plate.name}>
      <Solid faces={plate.faces} />
      <g transform={flat}>
        <text className="ds-map-plate-name">{plate.name}</text>
        {plate.note.split("\n").map((line, i) => (
          <text key={line} y={-30 * (i + 1)} className="ds-map-plate-note">
            {line}
          </text>
        ))}
      </g>
    </g>
  );
}

interface StationProps {
  station: TerrainStation;
  count: string | null;
  failures: number;
  health: StationHealth;
  compact: boolean;
  extra: string | null;
}

export const stationWords = ({ count, failures, health, extra }: Pick<StationProps, "count" | "failures" | "health" | "extra">) =>
  [count, failures > 0 ? `${formatCount(failures)} failed` : null, health.text, extra].filter(Boolean).join(", ");

function Station({ station, count, failures, health, compact, extra }: StationProps) {
  const failed = failures > 0;
  const { x } = station.top;
  const rows = stationRows(station.top, compact);
  return (
    <g className="ds-map-station" data-station={station.hop} data-health={health.state} data-failed={failed || undefined} data-plate={station.plate} data-anchor={place(station.anchor)}>
      <Solid faces={station.faces} />
      <text x={x} y={rows.title} className="ds-map-title">
        {station.label}
      </text>
      {compact ? null : (
        <>
          <text x={x} y={rows.count} className="ds-map-count">
            {[count, failed ? `${formatCount(failures)} failed` : null].filter(Boolean).join(", ")}
          </text>
          <text x={x} y={rows.health} className="ds-map-health">
            {health.text}
          </text>
        </>
      )}
      {extra && !compact ? (
        <text x={x} y={rows.extra} className="ds-map-health" data-extra>
          {extra}
        </text>
      ) : null}
    </g>
  );
}

/** Batches per state as blocks on one plate. Each block carries its number, so height is never the only reading. */
export function FlowMap({ counts, compact }: { counts: Record<BatchState, number>; compact?: boolean }) {
  const flow = useMemo(() => buildFlow(counts), [counts]);
  const summary = `Batches by step: ${BATCH_STATES.map((state) => `${formatCount(counts[state])} ${STATE_LABELS[state]}`).join(", ")}.`;
  return (
    <figure className="ds-diagram ds-flow-map" data-compact={compact || undefined}>
      <svg viewBox={flow.viewBox} role="img" aria-label={summary}>
        <g className="ds-map-plate">
          <Solid faces={flow.plate} />
        </g>
        {flow.routes.map((route) => (
          <polyline key={route} points={route} className="ds-map-route" />
        ))}
        {flow.blocks.map((block) => (
          <g key={block.state} className="ds-map-station" data-state-block={block.state} data-height={block.height.toFixed(2)} data-failed={(block.state === "Rejected" && block.count > 0) || undefined}>
            <Solid faces={block.faces} />
            <text x={block.top.x} y={block.top.y - (compact ? 46 : 42)} className="ds-map-title">
              {STATE_LABELS[block.state]}
            </text>
            <text x={block.top.x} y={block.top.y + 3} className="ds-map-count">
              {formatCount(block.count)}
            </text>
          </g>
        ))}
      </svg>
    </figure>
  );
}

function Pod({ pod, sending }: { pod: TerrainPod; sending: string | null }) {
  const { x, y } = pod.top;
  const mark = POD_MARK[pod.state];
  return (
    <g className="ds-map-pod" data-pod={pod.name} data-state={pod.state} data-service={pod.service} data-plate={pod.plate} data-anchor={place(pod.anchor)} data-sending={sending ? "" : undefined}>
      <Solid faces={pod.faces} />
      {mark ? (
        <text x={x} y={y + 5} className="ds-map-pod-mark">
          {mark}
        </text>
      ) : null}
      <text x={x} y={y - 12} className="ds-map-pod-label">
        {sending ?? pod.label}
      </text>
    </g>
  );
}

function Pad({ pad, count, compact }: { pad: TerrainPad; count: string | null; compact: boolean }) {
  const rows = padRows(pad, compact);
  return (
    <g className="ds-map-pad" data-plate={pad.plate} data-pad={pad.label}>
      <Solid faces={pad.faces} />
      <text x={rows.x} y={rows.label} className="ds-map-pad-label">
        {pad.label}
      </text>
      {count && !compact ? (
        <text x={rows.x} y={rows.words} className="ds-map-pad-label">
          {count}
        </text>
      ) : null}
    </g>
  );
}

const MORE_WORDS: Partial<Record<StationName, (counts: Record<HopName, number>) => string>> = {
  cosmos: (counts) => `${formatCount(counts.record)} records written`,
  bus: (counts) => `${formatCount(counts.deliver)} delivered`,
};

/** Words for the number on a station. */
export function stationCount(map: TerrainMap, block: TerrainStation, tally: MapCounts): string | null {
  if (!tally.counts) return null;
  const allPods = map.pods.filter((p) => p.service === block.hop).map((p) => p.name);
  const main = COUNT_WORDS[block.hop](laneCount(block.hop, block.pods, allPods, true, tally));
  const more = MORE_WORDS[block.hop]?.(tally.counts);
  return more ? `${main}, ${more}` : main;
}

/** Words for the number on a lane away from its service's home. */
export function padCount(map: TerrainMap, block: TerrainPad, tally: MapCounts): string | null {
  const service = map.pods.find((p) => p.name === block.pods[0])?.service;
  return tally.counts && (service === "dispatcher" || service === "worker" || service === "api") ? COUNT_WORDS[service](laneCount(service, block.pods, [], false, tally)) : null;
}
/** A phone scales the picture too far for words on it, so the same numbers stand under it as text. */
function MapWords({ rows }: { rows: { name: string; words: string }[] }) {
  return (
    <dl className="ds-map-words">
      {rows.map(({ name, words }) => (
        <div key={name}>
          <dt>{name}</dt>
          <dd>{words}</dd>
        </div>
      ))}
    </dl>
  );
}

/** Numbers on a lane first, then what the report says of a container. */
export function padLine(map: TerrainMap, block: TerrainPad, tally: MapCounts, words: Record<string, string>): string | null {
  return padCount(map, block, tally) ?? words[block.label] ?? null;
}

interface WordInput {
  map: TerrainMap;
  tally: MapCounts;
  failures: Record<HopName, number>;
  health: Record<StationName, StationHealth>;
  waiting: string | null;
  padWords: Record<string, string>;
}

export function wordRows({ map, tally, failures, health, waiting, padWords }: WordInput) {
  const station = (s: TerrainStation) => ({ name: s.label, words: stationWords({ count: stationCount(map, s, tally), failures: failures[stationHop(s.hop)], health: health[s.hop], extra: s.hop === "bus" ? waiting : null }) });
  const pad = (p: TerrainPad) => {
    const words = padLine(map, p, tally, padWords);
    return words ? [{ name: `${p.label} on ${p.plate}`, words }] : [];
  };
  return [...Object.values(map.stations).map(station), ...map.pads.flatMap(pad)];
}

export { MapWords, Pad, Plate, Pod, Station };
