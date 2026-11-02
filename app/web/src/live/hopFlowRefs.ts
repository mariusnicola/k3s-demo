import { useRef } from "react";
import { HOP_NAMES, type Hop, type HopName } from "../api/types";
import { DotField } from "../logic/flow/dots";
import { QueueWait, type WaitView } from "../logic/flow/queueWait";
import { HopDeduper } from "../logic/feed/hops";
import { PlaybackBuffer, type PlaybackSpeed } from "../logic/flow/playback";

function zeroCounts(): Record<HopName, number> {
  return Object.fromEntries(HOP_NAMES.map((n) => [n, 0])) as Record<HopName, number>;
}

export interface FlowSnapshot {
  counts: Record<HopName, number>;
  failures: Record<HopName, number>;
  /** Hops each pod reported, for the numbers per node. */
  podCounts: Record<string, number>;
  total: number;
  /** When these counts began, in ms since the epoch. */
  since: number | null;
  /** The Dispatcher pod that last sent to the queue. */
  sender: string | null;
  waiting: WaitView;
  recent: Hop[];
  undrawn: number;
  moving: number;
  behind: number;
  dropped: number;
  buffered: number;
  position: number | null;
}

export function emptySnapshot(): FlowSnapshot {
  return { counts: zeroCounts(), failures: zeroCounts(), podCounts: {}, total: 0, since: null, sender: null, waiting: { count: 0, oldestMs: null }, recent: [], undrawn: 0, moving: 0, behind: 0, dropped: 0, buffered: 0, position: null };
}

/** Everything the flow keeps out of React state, so a burst of hops costs no renders. */
export function useFlowRefs() {
  return {
    buffer: useRef(new PlaybackBuffer()),
    dots: useRef(new DotField()),
    deduper: useRef(new HopDeduper()),
    counts: useRef(zeroCounts()),
    failures: useRef(zeroCounts()),
    podCounts: useRef<Record<string, number>>({}),
    total: useRef(0),
    since: useRef<number | null>(null),
    sender: useRef<string | null>(null),
    queue: useRef(new QueueWait()),
    recent: useRef<Hop[]>([]),
    followed: useRef<Hop[]>([]),
    clock: useRef(0),
    lastFrame: useRef<number | null>(null),
    lastSnapshot: useRef(0),
    dirty: useRef(false),
    published: useRef({ moving: 0, undrawn: 0 }),
    realtimeRef: useRef(true),
    speedRef: useRef<PlaybackSpeed>(1),
    followRef: useRef<string | null>(null),
  };
}

export type FlowRefs = ReturnType<typeof useFlowRefs>;

export function resetFlowView({ dots, counts, failures, podCounts, total, since, sender, queue, recent, followed, dirty }: FlowRefs): void {
  dots.current.clear();
  counts.current = zeroCounts();
  failures.current = zeroCounts();
  podCounts.current = {};
  total.current = 0;
  since.current = null;
  sender.current = null;
  queue.current.clear();
  recent.current = [];
  followed.current = [];
  dirty.current = true;
}
