import { createContext, createElement, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { HOPS_WS, LIVE_PUBLISH_MS } from "../config";
import type { Tagged } from "../api/client";
import type { Batch, Hop } from "../api/types";
import { useSession } from "../auth/session";
import { applyHopFrame, emptyHopState, mergeBatches, parseHopFrame, type HopFrame, type HopState } from "../logic/feed/live";
import { trailingThrottle } from "../logic/throttle";
import { useLiveSocket, type Feed } from "./useLiveSocket";

const HOPS_KEPT = 500;

interface HopValue {
  feed: Feed;
  state: HopState;
  listen: (onHops: (hops: Hop[]) => void) => () => void;
}

const HopContext = createContext<HopValue | null>(null);

function useHopStore() {
  const [state, setState] = useState(emptyHopState);
  const current = useRef(state);
  const listeners = useRef(new Set<(hops: Hop[]) => void>());
  const recent = useRef<Hop[]>([]);
  const publish = useRef(trailingThrottle(() => setState(current.current), LIVE_PUBLISH_MS));
  useEffect(() => {
    const pending = publish.current;
    return () => pending.cancel();
  }, []);

  const onFrame = useCallback((frame: HopFrame) => {
    if ("hops" in frame && frame.hops.length > 0) {
      recent.current = [...recent.current, ...frame.hops].slice(-HOPS_KEPT);
      listeners.current.forEach((listener) => listener(frame.hops));
    }
    const next = applyHopFrame(current.current, frame);
    if (next === current.current) return;
    current.current = next;
    publish.current();
  }, []);
  return { state, setState, current, listeners, recent, publish, onFrame };
}

type HopStore = ReturnType<typeof useHopStore>;

/** Signing out closes the socket; one user's rows must never reach the next. */
function useClearOnSignOut(url: string | null, { setState, current, recent, publish }: HopStore): void {
  useEffect(() => {
    if (url) return;
    publish.current.cancel();
    current.current = emptyHopState();
    recent.current = [];
    setState(current.current);
  }, [url]);
}

function useHopListen({ listeners, recent }: HopStore): HopValue["listen"] {
  return useCallback((onHops: (hops: Hop[]) => void) => {
    listeners.current.add(onHops);
    if (recent.current.length > 0) onHops(recent.current);
    return () => {
      listeners.current.delete(onHops);
    };
  }, []);
}

/** One socket above the routes, so moving between pages costs no reconnect. */
export function HopProvider({ children }: { children: ReactNode }) {
  const { status } = useSession();
  const url = status === "signed-in" ? HOPS_WS : null;
  const store = useHopStore();
  const { state, onFrame } = store;
  const feed = useLiveSocket(url, parseHopFrame, onFrame);
  useClearOnSignOut(url, store);
  const listen = useHopListen(store);
  const value = useMemo(() => ({ feed, state, listen }), [feed, state, listen]);
  return createElement(HopContext.Provider, { value }, children);
}

function useHops(): HopValue {
  const value = useContext(HopContext);
  if (!value) throw new Error("HopProvider is missing");
  return value;
}

export function useHopFeed(onHops: (hops: Hop[]) => void): Feed {
  const { feed, listen } = useHops();
  const latest = useRef(onHops);
  latest.current = onHops;
  useEffect(() => listen((hops) => latest.current(hops)), [listen]);
  return feed;
}

export function useHopState(): { state: HopState; feed: Feed } {
  const { state, feed } = useHops();
  return { state, feed };
}

/** `base` is the one read of this list; pushed rows keep it current from then on. */
export function useLiveBatches(base: Batch[] | undefined, matches: (batch: Batch) => boolean, limit: number): Batch[] | undefined {
  const { state, feed } = useHops();
  // Busy lines push rows out of the kept map, and without this one would fall back to its older first read.
  const kept = useRef<{ base: Batch[]; merged: Batch[] } | null>(null);
  if (!base) {
    kept.current = null;
    return undefined;
  }
  const merged = mergeBatches(kept.current?.base === base ? kept.current.merged : base, state.rows, matches, limit, feed.state.status !== "lost");
  kept.current = { base, merged };
  return merged;
}

/** The newest copy of one batch among its first read, a write reply and the pushed row. */
export function useLiveBatch(farmId: string, id: string, ...known: (Tagged<Batch> | null | undefined)[]): Tagged<Batch> | undefined {
  const { state } = useHops();
  const row = state.rows.get(`${farmId}/${id}`);
  // Kept for the same reason as in useLiveBatches: the row can leave the map while this page stays open.
  const kept = useRef<Tagged<Batch> | undefined>(undefined);
  let best: Tagged<Batch> | undefined;
  for (const option of [kept.current, ...known, row ? { data: row.batch, etag: row.etag } : null]) {
    if (option && option.data.farmId === farmId && option.data.id === id && (!best || option.data.version > best.data.version)) best = option;
  }
  kept.current = best;
  return best;
}
