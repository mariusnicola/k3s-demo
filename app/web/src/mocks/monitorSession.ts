import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, vi } from "vitest";
import type { ClusterOperation } from "../api/monitorTypes";
import { MONITOR_STALE_MS, MONITOR_WS } from "../config";
import { setSocketFactory } from "../live/useLiveSocket";
import { REPORT, USERS, adminMonitorFrames, hopSnapshot, lineSnapshot, visitorMonitorFrames } from "./fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Handler, type Reply } from "./harness";

const SIGNED_OUT = { status: 401, body: { title: "Unauthorized" } };
export const SIGN_IN = "Sign in to Batch Tracker";
export const UNKNOWN = "Cluster work state unknown";
export const NOW = Date.now();
export const AT = new Date(NOW).toISOString();
export const OPERATION: ClusterOperation = { id: "one", name: "Deploy the app", step: "Charts", startedAt: AT, lastSeenAt: AT, state: "running", endedAt: null };
export const FRESH = { ...REPORT, checkedAt: AT };
export const CHANGE = { time: AT, kind: "check", object: "the only change", old: null, new: null };

export const monitor = () => FakeSocket.exact(MONITOR_WS);
export const monitorSockets = () => FakeSocket.all.filter((socket) => socket.url === MONITOR_WS);
export const openSockets = () => monitorSockets().filter((socket) => !socket.closed);

/** The browser sends the admin cookie until a logout's answer clears it. */
let cookie = false;
/** The monitor judges a socket once, by the cookie on its handshake, whatever happens to the cookie afterwards. */
const handshakes = new WeakMap<FakeSocket, boolean>();
export const hasCookie = () => cookie;
export const dropCookie = () => void (cookie = false);
export const fromServer = () => (handshakes.get(monitor()) ? adminMonitorFrames([], [], FRESH) : visitorMonitorFrames());

export function useCookieSockets() {
  useFakeSockets();
  setSocketFactory((url) => {
    const socket = new FakeSocket(url);
    handshakes.set(socket, cookie);
    return socket;
  });
}

/** A later task, so the page has already acted on the press by the time the cookie changes. */
const answered = (next: boolean, reply: Reply) =>
  new Promise<Reply>((resolve) =>
    setTimeout(() => {
      cookie = next;
      resolve(reply);
    }, 0),
  );

export function server(me: Parameters<typeof meRoute>[0] | null, routes: Record<string, Reply | Handler> = {}) {
  cookie = me === "ada";
  FakeSocket.greet("/ws/hops", hopSnapshot());
  FakeSocket.greet("/line/ws", lineSnapshot());
  installApi({
    "GET /api/me": me ? meRoute(me) : SIGNED_OUT,
    "GET /api/batches": { body: [] },
    "POST /api/auth/logout": () => answered(false, { status: 204 }),
    "POST /api/auth/dev-login": () => answered(true, { body: USERS.ada }),
    ...routes,
  });
}

export async function adminDashboard(routes: Record<string, Reply | Handler> = {}) {
  server("ada", routes);
  renderApp("/dashboard");
  await screen.findByRole("heading", { name: "Architecture" });
}

export async function signOut() {
  await userEvent.click(screen.getByRole("button", { name: /Switch user/ }));
  await screen.findByRole("heading", { name: SIGN_IN });
}

export const reopened = () => waitFor(() => expect(openSockets()).toHaveLength(1));

export async function signIn() {
  await userEvent.click(await screen.findByRole("button", { name: /Continue as ada/ }));
}

export const frames = (...sent: unknown[]) => act(async () => sent.forEach((frame) => monitor().send(frame)));
/** Past the age test, and long enough for the clock's next tick to redraw it. */
export async function pastStale() {
  vi.spyOn(Date, "now").mockReturnValue(NOW + MONITOR_STALE_MS + 60_000);
  await act(() => new Promise((resolve) => setTimeout(resolve, 1100)));
}
