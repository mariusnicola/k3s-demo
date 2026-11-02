import { useCallback, useState, type Dispatch, type SetStateAction } from "react";
import type { Hop } from "../api/types";
import type { DotFrame } from "../logic/flow/dots";
import { liveHops } from "../logic/feed/hops";
import { podService } from "../logic/terrain/pods";
import type { PlaybackSpeed } from "../logic/flow/playback";
import { emptySnapshot, resetFlowView, useFlowRefs, type FlowRefs, type FlowSnapshot } from "./hopFlowRefs";

const SNAPSHOT_MS = 250;
const RECENT_KEPT = 200;
/** A hidden tab stops animation frames; playback must not jump over what it missed. */
const MAX_FRAME_STEP_MS = 250;

export interface HopFlow {
  snapshot: FlowSnapshot;
  realtime: boolean;
  setRealtime: (on: boolean) => void;
  speed: PlaybackSpeed;
  setSpeed: (speed: PlaybackSpeed) => void;
  follow: string | null;
  /** `seen` is what the screen shows, which can hold rows already gone from the kept window. */
  setFollow: (traceId: string | null, seen?: readonly Hop[]) => void;
  restart: () => void;
  ingest: (hops: Hop[]) => void;
  getFrame: (now: number) => DotFrame[];
}

function publishSnapshot({ buffer, dots, counts, failures, podCounts, total, since, sender, queue, recent, followed, dirty, published, realtimeRef, followRef }: FlowRefs, setSnapshot: (snapshot: FlowSnapshot) => void): void {
  dirty.current = false;
  published.current = { moving: dots.current.active, undrawn: dots.current.undrawn };
  const playback = !realtimeRef.current;
  setSnapshot({
    counts: { ...counts.current },
    failures: { ...failures.current },
    podCounts: { ...podCounts.current },
    total: total.current,
    since: since.current,
    sender: sender.current,
    waiting: queue.current.view(Date.now()),
    recent: [...(followRef.current ? followed.current : recent.current)],
    undrawn: dots.current.undrawn,
    moving: dots.current.active,
    behind: playback ? buffer.current.remaining : 0,
    dropped: playback ? buffer.current.droppedUnplayed : 0,
    buffered: buffer.current.size,
    position: playback ? buffer.current.position : null,
  });
}

function tally({ counts, failures, podCounts, total, since, sender, queue }: FlowRefs, hops: readonly Hop[]): void {
  for (const hop of hops) {
    counts.current[hop.hop] += 1;
    if (!hop.ok) failures.current[hop.hop] += 1;
    if (hop.pod) podCounts.current[hop.pod] = (podCounts.current[hop.pod] ?? 0) + (podService(hop.pod) === hop.hop ? 1 : 0);
    if (hop.hop === "bus" && hop.pod) sender.current = hop.pod;
  }
  total.current += hops.length;
  since.current ??= Date.now();
  queue.current.note(hops);
}

function presentHops(flow: FlowRefs, hops: readonly Hop[]): void {
  const { dots, recent, followed, clock, dirty, followRef } = flow;
  if (hops.length === 0) return;
  tally(flow, hops);
  const shown = followRef.current ? hops.filter((h) => h.traceId === followRef.current) : hops;
  dots.current.ingest(liveHops(shown), clock.current);
  recent.current = [...hops.slice(-RECENT_KEPT).reverse(), ...recent.current].slice(0, RECENT_KEPT);
  // Kept apart: other traces would push a followed trace out of `recent` within seconds, and releasing must restore them.
  if (followRef.current) followed.current = [...shown.slice(-RECENT_KEPT).reverse(), ...followed.current].slice(0, RECENT_KEPT);
  dirty.current = true;
}

function advanceClock({ buffer, clock, lastFrame, realtimeRef, speedRef }: FlowRefs, now: number, present: (hops: readonly Hop[]) => void): void {
  const step = lastFrame.current === null ? 0 : now - lastFrame.current;
  lastFrame.current = now;
  if (realtimeRef.current) {
    clock.current += step;
  } else {
    const bounded = Math.min(step, MAX_FRAME_STEP_MS);
    clock.current += bounded * speedRef.current;
    present(buffer.current.advance(bounded, speedRef.current));
  }
}

function ingestHops({ buffer, deduper, dirty, realtimeRef }: FlowRefs, hops: Hop[], present: (hops: readonly Hop[]) => void): void {
  const fresh = deduper.current.fresh(hops);
  buffer.current.push(fresh);
  if (realtimeRef.current) present(fresh);
  else dirty.current = fresh.length > 0 || dirty.current;
}

function drawFrame(flow: FlowRefs, now: number, present: (hops: readonly Hop[]) => void, publish: () => void): DotFrame[] {
  const { dots, clock, lastSnapshot, dirty, published } = flow;
  advanceClock(flow, now, present);
  const frames = dots.current.frame(clock.current);
  // Dots also end by themselves, with no hop to mark the snapshot out of date.
  if (dots.current.active !== published.current.moving || dots.current.undrawn !== published.current.undrawn) dirty.current = true;
  if (dirty.current && now - lastSnapshot.current >= SNAPSHOT_MS) {
    lastSnapshot.current = now;
    publish();
  }
  return frames;
}

function useFlowData(flow: FlowRefs, setSnapshot: Dispatch<SetStateAction<FlowSnapshot>>) {
  const publish = useCallback(() => publishSnapshot(flow, setSnapshot), []);
  const present = useCallback((hops: readonly Hop[]) => presentHops(flow, hops), []);
  const resetView = useCallback(() => resetFlowView(flow), []);
  const ingest = useCallback((hops: Hop[]) => ingestHops(flow, hops, present), [present]);
  const getFrame = useCallback((now: number) => drawFrame(flow, now, present, publish), [present, publish]);
  return { publish, resetView, ingest, getFrame };
}

function usePlaybackControls({ buffer, realtimeRef, speedRef }: FlowRefs, { publish, resetView }: ReturnType<typeof useFlowData>, setRealtimeState: (on: boolean) => void, setSpeedState: (speed: PlaybackSpeed) => void) {
  const setRealtime = useCallback(
    (on: boolean) => {
      realtimeRef.current = on;
      setRealtimeState(on);
      resetView();
      if (on) buffer.current.stop();
      else buffer.current.start();
      publish();
    },
    [publish, resetView],
  );

  const restart = useCallback(() => {
    resetView();
    buffer.current.start();
    publish();
  }, [publish, resetView]);

  const setSpeed = useCallback((value: PlaybackSpeed) => {
    speedRef.current = value;
    setSpeedState(value);
  }, []);
  return { setRealtime, restart, setSpeed };
}

function useFollowControl({ dots, recent, followed, followRef }: FlowRefs, { publish }: ReturnType<typeof useFlowData>, setFollowState: (traceId: string | null) => void): HopFlow["setFollow"] {
  return useCallback(
    (traceId: string | null, seen: readonly Hop[] = []) => {
      followRef.current = traceId;
      setFollowState(traceId);
      dots.current.clear();
      const known = recent.current.filter((h) => h.traceId === traceId);
      followed.current = known.length > 0 ? known : seen.filter((h) => h.traceId === traceId);
      publish();
    },
    [publish],
  );
}

/** Holds hop data in refs and publishes four snapshots a second, so 500 hops a tick never drive 500 renders. */
export function useHopFlow(): HopFlow {
  const flow = useFlowRefs();
  const [snapshot, setSnapshot] = useState<FlowSnapshot>(emptySnapshot);
  const [realtime, setRealtimeState] = useState(true);
  const [speed, setSpeedState] = useState<PlaybackSpeed>(1);
  const [follow, setFollowState] = useState<string | null>(null);
  const data = useFlowData(flow, setSnapshot);
  const { setRealtime, restart, setSpeed } = usePlaybackControls(flow, data, setRealtimeState, setSpeedState);
  const setFollow = useFollowControl(flow, data, setFollowState);
  return { snapshot, realtime, setRealtime, speed, setSpeed, follow, setFollow, restart, ingest: data.ingest, getFrame: data.getFrame };
}
