import { useEffect, useId, useMemo, useRef, type ReactNode, type RefObject } from "react";
import type { HopName, StationName } from "../../api/types";
import { type PodReport } from "../../api/monitorTypes";
import { MAX_DOTS, type DotFrame } from "../../logic/flow/dots";
import type { StationHealth } from "../../logic/feed/hops";
import { legPath, pointOnPath } from "../../logic/terrain/links";
import { buildTerrain, podSummary } from "../../logic/terrain/terrain";
import { useReducedMotion } from "../theme";
import { MapWords, Pad, Plate, Pod, Station, padLine, place, stationCount, stationHop, wordRows } from "./Blocks";
import { PartTip } from "./PartTip";
import { partTips, type Tip } from "./partTips";
import { useMapTip } from "./useMapTip";

export { FlowMap, STATION_LABELS } from "./Blocks";

export interface ArchitectureDiagramProps {
  /** Pulled once per animation frame, so moving dots never re-render React. */
  getFrame: (now: number) => DotFrame[];
  /** Null in playback, where counts cover played hops only and would read as the system's. */
  counts: Record<HopName, number> | null;
  failures: Record<HopName, number>;
  podCounts?: Record<string, number>;
  health: Record<StationName, StationHealth>;
  /** The Dispatcher pod that does the work. */
  sender?: string | null;
  /** The sender comes from the Lease, not only from the last hop. */
  leader?: boolean;
  /** Words under a container, by its label. */
  padWords?: Record<string, string>;
  /** What waits in Service Bus, in words. */
  waiting?: string | null;
  compact?: boolean;
  /** Text alternative for the moving picture. */
  summary: string;
  lost?: boolean;
  /** Null means no report yet, so the map cannot say what runs where. */
  pods?: PodReport[] | null;
  /** The pods shown come from a report that is no longer current. */
  stale?: boolean;
  /** The store the report names. */
  store?: string | null;
}

interface DotPlaces {
  map: ReturnType<typeof buildTerrain>;
  turns: boolean;
  sender: string | null;
}

function drawDot(el: SVGGElement, dot: DotFrame | undefined, places: DotPlaces, reduced: boolean) {
  if (!dot) {
    // Every frame calls this for the whole pool, and a write of the same value still queues a record for every observer.
    if (el.getAttribute("visibility") !== "hidden") el.setAttribute("visibility", "hidden");
    return;
  }
  const t = reduced ? 1 : dot.progress;
  el.setAttribute("transform", `translate(${place(pointOnPath(legPath(places.map, dot, places.sender), t, places.turns))})`);
  el.setAttribute("data-ok", String(dot.ok));
  el.setAttribute("data-trace", dot.traceId);
  el.setAttribute("data-leg", `${dot.from ?? "start"}>${dot.to}`);
  el.setAttribute("visibility", "visible");
}

function useDotPool(getFrame: ArchitectureDiagramProps["getFrame"], places: DotPlaces, reduced: boolean) {
  const pool = useRef<(SVGGElement | null)[]>([]);
  const frameSource = useRef(getFrame);
  frameSource.current = getFrame;
  const latest = useRef(places);
  latest.current = places;

  useEffect(() => {
    let handle = 0;
    const draw = (now: number) => {
      const frames = frameSource.current(now);
      pool.current.forEach((el, i) => {
        if (el) drawDot(el, frames[i], latest.current, reduced);
      });
      handle = requestAnimationFrame(draw);
    };
    handle = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(handle);
  }, [reduced]);

  return pool;
}

function DotPool({ pool }: { pool: RefObject<(SVGGElement | null)[]> }) {
  return Array.from({ length: MAX_DOTS }, (_, i) => (
    <g
      key={i}
      ref={(el) => {
        pool.current[i] = el;
      }}
      visibility="hidden"
      className="ds-dot"
    >
      <circle r={13} className="ds-dot-halo" />
      <circle r={6} className="ds-dot-core" />
      <path d="M0 -8 8 0 0 8 -8 0Z" className="ds-dot-fail" />
    </g>
  ));
}

function Part({ tip, describedAs, children }: { tip: Tip; describedAs: string; children: ReactNode }) {
  return (
    <>
      {/* Beside the part, not in it: text inside would become part of its name. */}
      <desc id={describedAs}>{[tip.info.what, tip.info.does, tip.live].filter(Boolean).join(" ")}</desc>
      <g className="ds-map-part" data-part={tip.id} tabIndex={0} role="button" aria-label={tip.label} aria-describedby={describedAs}>
        {children}
      </g>
    </>
  );
}

function Blocks({ map, tips, base, input }: { map: ReturnType<typeof buildTerrain>; tips: Tip[]; base: string; input: Pick<ArchitectureDiagramProps, "failures" | "health" | "sender" | "leader" | "waiting" | "compact"> & { tally: Parameters<typeof stationCount>[2]; padWords: Record<string, string> } }) {
  const { failures, health, sender, leader, waiting, compact, tally, padWords } = input;
  return map.blocks.map((block, i) => (
    <Part key={tips[i]!.id} tip={tips[i]!} describedAs={`${base}-block-${i}`}>
      {block.kind === "station" ? (
        <Station station={block} count={stationCount(map, block, tally)} failures={failures[stationHop(block.hop)]} health={health[block.hop]} compact={Boolean(compact)} extra={block.hop === "bus" ? (waiting ?? null) : null} />
      ) : block.kind === "pod" ? (
        <Pod pod={block} sending={block.name === sender ? (leader ? "leader" : "sending") : null} />
      ) : (
        <Pad pad={block} count={padLine(map, block, tally, padWords)} compact={Boolean(compact)} />
      )}
    </Part>
  ));
}

export function ArchitectureDiagram({ getFrame, counts, failures, podCounts = {}, health, sender = null, leader, padWords = {}, waiting, compact, summary, lost, pods = null, stale, store = null }: ArchitectureDiagramProps) {
  const reduced = useReducedMotion();
  const map = useMemo(() => buildTerrain(pods, Boolean(compact), store), [pods, compact, store]);
  const pool = useDotPool(getFrame, { map, turns: !compact, sender }, reduced);
  const tally = { counts, podCounts };
  const label = [summary, podSummary(map)].filter(Boolean).join(" ");
  const base = useId();
  const { plates, blocks } = partTips({ map, tally, failures, health, waiting: waiting ?? null, padWords, sender, leader: Boolean(leader), stale: Boolean(stale), store });
  const { open, picture, tipHover } = useMapTip();
  const shown = open && [...plates, ...blocks].find((tip) => tip.id === open.part);

  return (
    <figure className="ds-diagram" data-lost={lost || undefined} data-reduced={reduced || undefined} data-stale={(stale && pods !== null) || undefined} data-compact={compact || undefined}>
      <svg viewBox={Object.values(map.viewBox).join(" ")} role="group" aria-label={label} {...picture}>
        {map.plates.map((plate, i) => (
          <Part key={plate.name} tip={plates[i]!} describedAs={`${base}-plate-${i}`}>
            <Plate plate={plate} />
          </Part>
        ))}
        {map.links.map((link) => (
          <path key={link.id} d={link.d} className="ds-map-link" data-link={link.id} />
        ))}
        <Blocks map={map} tips={blocks} base={base} input={{ failures, health, sender, leader, waiting, compact, tally, padWords }} />
        <DotPool pool={pool} />
      </svg>
      {compact ? <MapWords rows={wordRows({ map, tally, failures, health, waiting: waiting ?? null, padWords })} /> : null}
      {open && shown ? <PartTip id={`${base}-tip`} info={shown.info} live={shown.live} anchor={open.anchor} {...tipHover} /> : null}
    </figure>
  );
}
