import { act, render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { vi } from "vitest";
import type { ReactElement } from "react";
import { App } from "../App";
// With the test file, not inside a find: a lazy page's first load can outlast the wait.
import "../pages/dashboard/DashboardPage";
import "../pages/design-system/DesignSystemPage";
import "../pages/logs/LogsPage";
import { setRetryWait } from "../api/retry";
import { SessionProvider } from "../auth/session";
import { setLostShownAfter, setSocketFactory } from "../live/useLiveSocket";
import type { MinimalSocket } from "../live/socket";
import { serviceLogs, USERS } from "./fixtures";

export class FakeSocket implements MinimalSocket {
  static all: FakeSocket[] = [];
  private static greetings = new Map<string, unknown>();
  onopen: MinimalSocket["onopen"] = null;
  onclose: MinimalSocket["onclose"] = null;
  onerror: MinimalSocket["onerror"] = null;
  onmessage: MinimalSocket["onmessage"] = null;
  closed = false;

  constructor(readonly url: string) {
    FakeSocket.all.push(this);
    for (const [part, frame] of FakeSocket.greetings) {
      if (!url.endsWith(part)) continue;
      // After the caller attached its handlers, as a real server's first frame would be.
      setTimeout(() => {
        if (this.closed) return;
        act(() => {
          this.open();
          this.send(frame);
        });
      }, 0);
    }
  }

  /** Every socket on this path opens and sends this frame, like the snapshot a server sends on connect. */
  static greet(part: string, frame: unknown) {
    FakeSocket.greetings.set(part, frame);
  }

  close() {
    this.closed = true;
  }
  open() {
    this.onopen?.(undefined as never);
  }
  send(data: unknown) {
    this.onmessage?.({ data: JSON.stringify(data) });
  }
  sendHops(hops: unknown[]) {
    this.send({ type: "hops", hops });
  }
  drop(code = 1006) {
    this.onclose?.({ code });
  }

  /** The monitor URL is a prefix of the hop URL, so it needs an exact match. */
  static exact(url: string): FakeSocket {
    const found = [...FakeSocket.all].reverse().find((s) => s.url === url && !s.closed);
    if (!found) throw new Error(`no open socket at ${url}`);
    return found;
  }

  static find(part: string): FakeSocket {
    const found = [...FakeSocket.all].reverse().find((s) => s.url.includes(part) && !s.closed);
    if (!found) throw new Error(`no open socket for ${part}`);
    return found;
  }

  static reset() {
    FakeSocket.all = [];
    FakeSocket.greetings.clear();
  }
}

export interface Reply {
  status?: number;
  body?: unknown;
  etag?: string;
  headers?: Record<string, string>;
}

export type Handler = (url: string, init: RequestInit) => Reply | Promise<Reply>;

export interface ApiCall {
  method: string;
  url: string;
  headers: Record<string, string>;
  body: unknown;
}

/** What the server's session read answers for a test that names who `/api/me` knows. */
function sessionFrom(reply: Reply): Reply {
  if (reply.status === 401) return { body: { me: null } };
  return (reply.status ?? 200) === 200 ? { ...reply, body: { me: reply.body } } : reply;
}

/** Fake transport under the real client: routes are "METHOD path-prefix", first match wins. */
export function installApi(routes: Record<string, Reply | Handler>) {
  setRetryWait(async () => undefined);
  const calls: ApiCall[] = [];
  const answers: Record<string, Reply | Handler> = { "GET /ingress/logs": { body: { correlationId: "", available: true, since: null, rows: [] } }, "GET /api/me": { status: 401 }, "GET /metrics/series": { body: { bucket: "1m", available: true, error: null, historyStart: null, series: [] } }, "GET /live/logs": { body: serviceLogs({ matched: 0, entries: [] }) }, ...routes };
  const stub = vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const url = String(input);
    const method = init.method ?? "GET";
    calls.push({ method, url, headers: (init.headers ?? {}) as Record<string, string>, body: init.body ? JSON.parse(String(init.body)) : undefined });
    const sessionRead = url.includes("/api/session") && !answers["GET /api/session"];
    const match = Object.keys(answers)
      .filter((key) => {
        const [m, path] = key.split(" ");
        return sessionRead ? key === "GET /api/me" : m === method && url.includes(path!);
      })
      .sort((a, b) => b.length - a.length)[0];
    if (!match) return new Response(JSON.stringify({ title: `no mock for ${method} ${url}` }), { status: 500 });
    const route = answers[match]!;
    const answer = typeof route === "function" ? await route(url, init) : route;
    const reply = sessionRead && match === "GET /api/me" ? sessionFrom(answer) : answer;
    const headers = new Headers(reply.headers);
    if (reply.etag) headers.set("ETag", reply.etag);
    return new Response(reply.body === undefined ? null : JSON.stringify(reply.body), { status: reply.status ?? 200, headers });
  });
  vi.stubGlobal("fetch", stub);
  return calls;
}

export function useFakeSockets() {
  FakeSocket.reset();
  setLostShownAfter(0);
  setSocketFactory((url) => new FakeSocket(url));
}

export function renderApp(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <SessionProvider>
        <App />
      </SessionProvider>
    </MemoryRouter>,
  );
}

export function renderInRouter(element: ReactElement) {
  return render(
    <MemoryRouter>
      <SessionProvider>{element}</SessionProvider>
    </MemoryRouter>,
  );
}

export function meRoute(user: keyof typeof USERS): Reply {
  return { body: USERS[user] };
}
