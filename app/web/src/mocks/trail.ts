import { screen } from "@testing-library/react";
import type { LogEntry } from "../api/types";
import { LINE_STOPPED, lineLogs, lineSnapshot, serviceEntry, serviceLogs } from "./fixtures";
import { FakeSocket, installApi, meRoute, renderApp, type ApiCall, type Handler, type Reply } from "./harness";

export const ID = "6f1c2a34-1b2c-4d3e-8f90-a1b2c3d4e5f6";
export const CORRELATION = "ab".repeat(16);
export const LIVE = "GET /live/logs";
export const LINE = "GET /line/logs";
export const INGRESS = "GET /ingress/logs";
export const PAGE = `/logs?reportId=${ID}`;
export const SINCE = "2026-10-31T08:00:00.000Z";
export const SERVED = { at: "2026-10-31T08:59:59.000Z", status: 200, upstreamStatus: null as string | null, path: "/api/batches" };

export function report(overrides: Partial<LogEntry> = {}): LogEntry {
  return serviceEntry({
    service: "browser",
    reason: "browser-failure",
    message: "GET /api/batches: Down",
    at: "2026-10-31T09:00:00.000Z",
    reportId: ID,
    correlationId: CORRELATION,
    kind: "request-failure",
    route: "/batches/farm-a/b1",
    httpStatus: 503,
    ...overrides,
  });
}

export const found = (...entries: LogEntry[]) => ({ body: serviceLogs({ matched: entries.length, entries }) });
export const ingress = (rows: unknown[], since: string | null = SINCE) => ({ body: { correlationId: CORRELATION, available: true, since, rows } });
export const NOT_FOLLOWED = { status: 503, body: { correlationId: CORRELATION, available: false, since: null, rows: [] } };

export function open(routes: Record<string, Reply | Handler>, path = PAGE) {
  const calls = installApi({ "GET /api/me": meRoute("ada"), [LINE]: { body: lineLogs({ matched: 0, entries: [], byReason: {} }) }, [LIVE]: found(report()), [INGRESS]: ingress([]), ...routes });
  FakeSocket.greet("/line/ws", lineSnapshot({ status: LINE_STOPPED }));
  renderApp(path);
  return calls;
}

export const reads = (calls: ApiCall[], part: string) => calls.filter((call) => call.url.includes(part)).map((call) => new URLSearchParams(call.url.split("?")[1]));
export const trail = () => screen.findByRole("region", { name: "Ingress" });
