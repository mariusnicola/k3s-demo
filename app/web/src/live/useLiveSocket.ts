import { useCallback, useEffect, useRef, useState } from "react";
import { client, endSession } from "../api/client";
import { LiveSocket, type MinimalSocket, type SocketState } from "./socket";
import { LOST_SHOWN_AFTER_MS, SteadyStatus } from "./status/steadyStatus";

let createSocket: ((url: string) => MinimalSocket) | undefined;
let lostShownAfter = LOST_SHOWN_AFTER_MS;

/** Test seam: a test of what a lost page says should not wait the real time. */
export function setLostShownAfter(ms: number): void {
  lostShownAfter = ms;
}

/** Test seam: components keep the real LiveSocket, only the transport is swapped. */
export function setSocketFactory(factory: ((url: string) => MinimalSocket) | undefined): void {
  createSocket = factory;
}

export interface Feed {
  state: SocketState;
  reconnect: () => void;
}

function reconnectNow<T>(socket: LiveSocket<T> | null): void {
  socket?.reconnectNow();
  // A refused upgrade looks like a network drop, so only a call can tell that the session ended.
  client.me().catch(() => undefined);
}

function stopBoth(socket: { stop(): void }, steady: SteadyStatus): void {
  socket.stop();
  steady.stop();
}

const STARTING: SocketState = { status: "connecting", lastDataAt: null };

/**
 * A new `session` opens a new socket on the same address, because only a handshake carries the cookie.
 * With `onEnded` the socket outlives the session: it runs after a 4401, and the socket then retries.
 */
export function useLiveSocket<T>(url: string | null, parse: (raw: string) => T | null, onData: (data: T) => void, session = 0, onEnded?: () => void): Feed {
  const [state, setState] = useState<SocketState>(STARTING);
  const socketRef = useRef<LiveSocket<T> | null>(null);
  const latest = useRef(onData);
  latest.current = onData;

  useEffect(() => {
    // Cleared here, at sign out, or the next user's first render would show this user's open socket.
    setState(STARTING);
    if (!url) return;
    const steady = new SteadyStatus(setState, lostShownAfter);
    const socket = new LiveSocket<T>({
      url,
      parse,
      onData: (data) => latest.current(data),
      onUnauthorized: () => void (endSession(), onEnded?.()),
      outlivesSession: onEnded !== undefined,
      onState: (next) => steady.next(next),
      create: createSocket,
    });
    socketRef.current = socket;
    socket.start();
    return () => stopBoth(socket, steady);
  }, [url, parse, session]);

  const reconnect = useCallback(() => reconnectNow(socketRef.current), []);
  return { state, reconnect };
}
