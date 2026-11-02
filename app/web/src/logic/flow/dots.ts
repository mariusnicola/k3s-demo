import { HOP_NAMES, type Hop, type HopName, type HopStation } from "../../api/types";
import { podService } from "../terrain/pods";

export const MAX_DOTS = 80;
export const TRAVEL_MS = 450;
export const LINGER_MS = 900;

interface Leg {
  from: HopStation | null;
  fromPod: string | null;
  to: HopStation;
  toPod: string | null;
  /** The API pod a step passes on its way to the store. */
  through: string | null;
}

interface Dot extends Leg {
  traceId: string;
  startedAt: number;
  ok: boolean;
  queue: Hop[];
  newest: Hop;
}

export interface DotFrame {
  traceId: string;
  from: HopStation | null;
  fromPod?: string | null;
  to: HopStation;
  toPod?: string | null;
  through?: string | null;
  /** 0 at `from`, 1 at `to`. */
  progress: number;
  ok: boolean;
}

/** The leg of a hop from a pod that does not send `from` and `to` yet. */
const OLD_LEG: Record<HopName, [HopStation, HopStation]> = {
  // One hop now says accepted and saved, so it walks the whole way to the store.
  api: ["client", "cosmos"],
  dispatcher: ["cosmos", "dispatcher"],
  bus: ["dispatcher", "bus"],
  deliver: ["bus", "dispatcher"],
  worker: ["dispatcher", "worker"],
  record: ["worker", "cosmos"],
  settle: ["worker", "dispatcher"],
};

/** `pod` is the reporter, and a Dispatcher pod sends a bus hop, so a pod marks only the end it stands at. */
function legOf(hop: Hop): Leg {
  const [oldFrom, oldTo] = OLD_LEG[hop.hop];
  const from = hop.from ?? oldFrom;
  const to = hop.to ?? oldTo;
  const reporter = hop.pod ?? null;
  const at = (station: HopStation) => (reporter && podService(reporter) === station ? reporter : null);
  return { from, fromPod: at(from), to, toPod: at(to), through: from === "client" && to === "cosmos" ? reporter : null };
}

const place = (hop: Hop) => HOP_NAMES.indexOf(hop.hop);

function earlier(a: Hop, b: Hop): number {
  if (a.seq !== undefined && b.seq !== undefined) return a.seq - b.seq;
  return Date.parse(a.at) - Date.parse(b.at) || place(a) - place(b);
}

/** Services post hops separately, so arrival order means nothing. */
function realOrder(hops: readonly Hop[]): Hop[] {
  const traces = new Map<string, Hop[]>();
  for (const hop of hops) traces.set(hop.traceId, [...(traces.get(hop.traceId) ?? []), hop]);
  return [...traces.values()].flatMap((trace) => trace.sort(earlier));
}

function isStale(hop: Hop, newest: Hop): boolean {
  if (hop.seq !== undefined && newest.seq !== undefined) return hop.seq < newest.seq;
  return hop.at === newest.at && place(hop) < place(newest);
}

/** One dot per trace, walking the hops of that trace in order. Time is passed in, so playback can scale it. */
export class DotField {
  private dots = new Map<string, Dot>();
  private overflow = new Set<string>();

  constructor(private readonly maxDots: number = MAX_DOTS) {}

  get undrawn(): number {
    return this.overflow.size;
  }

  get active(): number {
    return this.dots.size;
  }

  clear(): void {
    this.dots.clear();
    this.overflow.clear();
  }

  ingest(hops: readonly Hop[], now: number): void {
    for (const hop of realOrder(hops)) {
      const dot = this.dots.get(hop.traceId);
      if (dot && isStale(hop, dot.newest)) {
        dot.ok = dot.ok && hop.ok;
      } else if (dot) {
        dot.queue.push(hop);
        dot.newest = hop;
      } else if (this.dots.size < this.maxDots) {
        this.dots.set(hop.traceId, { traceId: hop.traceId, ...legOf(hop), startedAt: now, ok: hop.ok, queue: [], newest: hop });
      } else {
        this.overflow.add(hop.traceId);
      }
    }
  }

  private step(dot: Dot, hop: Hop): void {
    const leg = legOf(hop);
    Object.assign(dot, leg, { fromPod: leg.fromPod ?? (dot.to === leg.from ? dot.toPod : null), startedAt: dot.startedAt + TRAVEL_MS, ok: dot.ok && hop.ok });
  }

  frame(now: number): DotFrame[] {
    const frames: DotFrame[] = [];
    for (const dot of this.dots.values()) {
      while (dot.queue.length > 0 && now - dot.startedAt >= TRAVEL_MS) this.step(dot, dot.queue.shift()!);
      const age = now - dot.startedAt;
      if (dot.queue.length === 0 && age >= TRAVEL_MS + LINGER_MS) {
        this.dots.delete(dot.traceId);
        continue;
      }
      frames.push({ traceId: dot.traceId, from: dot.from, fromPod: dot.fromPod, to: dot.to, toPod: dot.toPod, through: dot.through, progress: Math.min(1, age / TRAVEL_MS), ok: dot.ok });
    }
    if (this.dots.size < this.maxDots) this.overflow.clear();
    return frames;
  }
}
