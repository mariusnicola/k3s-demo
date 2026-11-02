import { act, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, describeProblem } from "../api/problem";
import { MONITOR_STALE_MS, MONITOR_WS } from "../config";
import { formatTime, newestTime } from "../logic/format";
import { counterNote } from "../logic/monitor/logs";
import { reportFresh } from "./dashboard/useMonitorReport";
import { LINE_STOPPED, REPORT, batch, hopSnapshot, lineLogs, lineSnapshot, logEntry, pending } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../mocks/harness";

const NOW = new Date(2026, 9, 31, 12, 0, 0).getTime();
const NO_FILTERS = { level: "", reason: "", farm: "", batch: "", correlationId: "", from: "", to: "" };

function adminDashboard() {
  installApi({ "GET /api/me": meRoute("ada"), "GET /api/batches": { body: [] } });
  renderApp("/dashboard");
}

beforeEach(() => useFakeSockets());
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("REQ-BT-045 the line under load", () => {
  it("REQ-BT-045 shows what is in flight and how often the API said wait", async () => {
    FakeSocket.greet("/line/ws", lineSnapshot({ status: { ...LINE_STOPPED, running: true, rate: 100, inFlight: 500, throttled: 3 } }));
    adminDashboard();
    expect(await screen.findByText("Slots in use", {}, { timeout: 5000 })).toBeInTheDocument();
    expect(screen.getByText("500")).toBeInTheDocument();
    expect(screen.getByText("Throttled")).toBeInTheDocument();
    expect(screen.getByText("3")).toBeInTheDocument();
  });
});

describe("REQ-BT-050 Updated is the newest of what was read and what was pushed", () => {
  it.each([
    [1, 2, 2],
    [2, 1, 2],
    [null, 2, 2],
    [1, null, 1],
    [null, null, null],
  ])("REQ-BT-050 between %s and %s it is %s", (a, b, newest) => {
    expect(newestTime(a, b)).toBe(newest);
  });

  it("REQ-BT-050 a pushed frame moves it on the batch page", async () => {
    const clock = vi.spyOn(Date, "now").mockReturnValue(NOW);
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches/farm-a/b-1": { etag: '"v1"', body: batch() } });
    renderApp("/batches/farm-a/b-1");
    await screen.findByRole("heading", { level: 1, name: batch().product });
    const socket = FakeSocket.find("/ws/hops");
    act(() => socket.open());
    const later = NOW + 3_600_000;
    clock.mockReturnValue(later);
    act(() => socket.send(hopSnapshot()));
    expect(await screen.findByText(`Updated ${formatTime(later)}`)).toBeInTheDocument();
  });
});

describe("REQ-BT-053 a delayed record while the socket is lost", () => {
  it("REQ-BT-053 does not say to wait for a change this page cannot see", async () => {
    const old = new Date(Date.now() - 600_000).toISOString();
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches/farm-a/b-1": { etag: '"v1"', body: batch({ pending: pending("sent", old) }) } });
    renderApp("/batches/farm-a/b-1");
    await screen.findByText("Compliance record is delayed");
    const socket = FakeSocket.find("/ws/hops");
    act(() => socket.open());
    await act(async () => socket.drop());
    expect(await screen.findByText(/may be behind/)).toBeInTheDocument();
    expect(screen.queryByText(/Nothing yet/)).toBeNull();
  });
});

describe("REQ-BT-030 when a report stops being fresh", () => {
  const at = new Date(NOW).toISOString();

  it("REQ-BT-030 the limit itself is still fresh, one past it is not, by frame age", () => {
    expect(reportFresh("open", NOW, at, NOW + MONITOR_STALE_MS)).toBe(true);
    expect(reportFresh("open", NOW, new Date(NOW + 9e6).toISOString(), NOW + MONITOR_STALE_MS + 1)).toBe(false);
  });

  it("REQ-BT-030 an old report on a live socket is stale, by report age", () => {
    expect(reportFresh("open", NOW + MONITOR_STALE_MS + 1, at, NOW + MONITOR_STALE_MS + 1)).toBe(false);
  });

  it("REQ-BT-030 nothing is fresh without an open socket, a frame and a report", () => {
    expect(reportFresh("lost", NOW, at, NOW)).toBe(false);
    expect(reportFresh("open", null, at, NOW)).toBe(false);
    expect(reportFresh("open", NOW, undefined, NOW)).toBe(false);
  });
});

describe("REQ-BT-030 panels agree when the monitor goes silent", () => {
  it("REQ-BT-030 Performance and Changes stop saying live", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    adminDashboard();
    await screen.findByRole("heading", { name: "Changes" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() => socket.send(REPORT));
    vi.setSystemTime(Date.now() + MONITOR_STALE_MS + 2000);
    await act(() => vi.advanceTimersByTimeAsync(1100));
    expect(screen.getByText(/Performance updates: data stale/)).toBeInTheDocument();
    expect(screen.queryByText(/, live\./)).toBeNull();
  });
});

describe("REQ-BT-045 the counter note", () => {
  it("REQ-BT-045 an error reason is limited to errors", () => {
    expect(counterNote(5, lineLogs({ matched: 2 }), { ...NO_FILTERS, reason: "http-refused" }).text).toMatch(/filters hide the rest/);
  });

  it("REQ-BT-045 an information reason is not", () => {
    expect(counterNote(5, lineLogs({ matched: 2 }), { ...NO_FILTERS, reason: "closed" }).text).toMatch(/not limited to errors/);
  });
});

describe("REQ-BT-045 the first snapshot of a page", () => {
  it("REQ-BT-045 offers no missed entries, for no outage happened", async () => {
    const at = "2026-10-31T09:00:00Z";
    installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": { body: lineLogs({ entries: [logEntry({ at })] }) } });
    renderApp("/logs");
    await screen.findByRole("table", { name: "Log entries" });
    const line = FakeSocket.find("/line/ws");
    act(() => line.open());
    act(() => line.send(lineSnapshot({ entries: [logEntry({ at: "2026-10-31T11:00:00Z", traceId: "newer" })], entriesDropped: 5 })));
    await waitFor(() => expect(screen.getAllByRole("row").length).toBeGreaterThan(1));
    expect(screen.queryByRole("button", { name: "Load missed entries" })).toBeNull();
  });
});

describe("REQ-BT-053 a server failure on work with no batch", () => {
  const failed = (url: string) => describeProblem(new ApiError(500, "Server error", null, { request: { method: "POST", url } }));

  it.each(["/line", "/api/compliance/run"])("REQ-BT-053 %s does not mention a batch", (url) => {
    expect(failed(url).next).not.toMatch(/batch/);
  });

  it("REQ-BT-053 a batch step still says to check the batch", () => {
    expect(failed("/api/batches/farm-a/b-1/inspect").next).toBe("Check the batch before repeating it.");
  });
});
