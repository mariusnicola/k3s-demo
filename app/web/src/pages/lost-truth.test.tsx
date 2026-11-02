import { act, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MONITOR_STALE_MS, MONITOR_WS } from "../config";
import { mergeBatches } from "../logic/feed/live";
import { formatTime, shownTime } from "../logic/format";
import { counterNote, entryGuidance } from "../logic/monitor/logs";
import { LINE_STOPPED, REPORT, batch, hopSnapshot, lineLogs, lineSnapshot, logEntry, pending, step } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../mocks/harness";

const NOW = new Date(2026, 9, 31, 12, 0, 0).getTime();
const NO_FILTERS = { level: "", reason: "", farm: "", batch: "", correlationId: "", from: "", to: "" };
const ERRORS = { ...NO_FILTERS, level: "error" };
const all = () => true;

function dropped(part: string) {
  const socket = FakeSocket.find(part);
  return { socket, drop: () => act(async () => socket.drop()) };
}

function adminDashboard() {
  installApi({ "GET /api/me": meRoute("ada"), "GET /api/batches": { body: [] }, "DELETE /line": { status: 204 } });
  renderApp("/dashboard");
}

beforeEach(() => useFakeSockets());
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("REQ-BT-050 a row frozen by a lost socket", () => {
  const row = { batch: batch({ id: "gone", version: 9 }), etag: '"v9"' };
  const rows = new Map([["farm-a/gone", row]]);

  it("REQ-BT-050 does not add itself to a list that was read after it", () => {
    expect(mergeBatches([], rows, all, 200, false)).toEqual([]);
  });

  it("REQ-BT-050 still updates a batch the list already holds", () => {
    expect(mergeBatches([batch({ id: "gone", version: 1 })], rows, all, 200, false)[0]!.version).toBe(9);
  });

  it("REQ-BT-050 a live row joins as before", () => {
    expect(mergeBatches([], rows, all, 200, true)).toHaveLength(1);
  });
});

describe("REQ-BT-053 the batch page after the socket returned", () => {
  it("REQ-BT-053 does not claim it updates by itself while the offer stands", async () => {
    const clock = vi.spyOn(Date, "now").mockReturnValue(NOW);
    const at = new Date(NOW - 1000).toISOString();
    const body = batch({ steps: [step("Dose1", false, { at })], pending: pending("sent", at) });
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches/farm-a/b-1": { etag: '"v1"', body } });
    renderApp("/batches/farm-a/b-1");
    await screen.findByText("Event in flight");
    const first = dropped("/ws/hops");
    act(() => first.socket.open());
    await first.drop();
    FakeSocket.greet("/ws/hops", hopSnapshot());
    clock.mockReturnValue(NOW + 3_600_000);
    await screen.findByRole("button", { name: "Reconnect now" }).then(userEvent.click);
    await screen.findByRole("button", { name: "Read again" });
    expect(await screen.findByText(/may be behind/)).toBeInTheDocument();
    expect(screen.queryByText(/updates by itself/)).toBeNull();
    expect(screen.getByText(`Updated ${formatTime(NOW)}`)).toBeInTheDocument();
  });

  it.each([
    [true, 5, 9, 5],
    [true, null, 9, 9],
    [false, 5, 9, 9],
  ])("REQ-BT-050 missed %s, read %s, pushed %s shows %s", (missed, read, pushed, shown) => {
    expect(shownTime(missed, read, pushed)).toBe(shown);
  });
});

describe("REQ-BT-045 Stop line while the line socket is lost", () => {
  it("REQ-BT-045 says the stop was accepted and the state is unknown", async () => {
    adminDashboard();
    await screen.findByRole("heading", { name: "Production line" }, { timeout: 5000 });
    const line = dropped("/line/ws");
    act(() => line.socket.open());
    act(() => line.socket.send(lineSnapshot({ status: { ...LINE_STOPPED, running: true, rate: 100 } })));
    await line.drop();
    await screen.findByText(/Last known: running at 100 per second/);
    await userEvent.click(screen.getByRole("button", { name: "Stop line" }));
    expect(await screen.findByText(/Stop accepted/)).toBeInTheDocument();
    expect(screen.queryByText(/Last known: running/)).toBeNull();
  });
});

describe("REQ-BT-045 the counter note", () => {
  it("REQ-BT-045 entries skipped under load are not blamed on memory", () => {
    const text = counterNote(5, lineLogs({ matched: 2, evicted: 10 }), ERRORS, 3).text;
    expect(text).not.toMatch(/left memory/);
    expect(text).toMatch(/left out/);
  });

  it("REQ-BT-045 a reason that holds every error matches", () => {
    expect(counterNote(5, lineLogs({ matched: 5 }), { ...NO_FILTERS, reason: "http-refused" }).matches).toBe(true);
  });

  it("REQ-BT-045 with entries gone from memory the filters are not the only cause", () => {
    expect(counterNote(5, lineLogs({ matched: 3, evicted: 4 }), { ...NO_FILTERS, reason: "http-refused" }).text).toMatch(/left memory/);
  });

  it("REQ-BT-045 the list says it has a hole", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": { body: lineLogs() } });
    renderApp("/logs");
    await screen.findByRole("table", { name: "Log entries" });
    const line = dropped("/line/ws");
    act(() => line.socket.open());
    act(() => line.socket.send({ type: "line", at: "2026-10-31T11:00:00Z", status: LINE_STOPPED, entries: [], entriesDropped: 12, evicted: 0 }));
    expect(await screen.findByText(/left out of this list/)).toBeInTheDocument();
  });

  it("REQ-BT-045 a frozen counter is not compared", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": { body: lineLogs({ entries: [logEntry()], matched: 9 }) } });
    renderApp("/logs?level=error");
    await screen.findByRole("table", { name: "Log entries" });
    const line = dropped("/line/ws");
    act(() => line.socket.open());
    act(() => line.socket.send(lineSnapshot()));
    await line.drop();
    expect(await screen.findByText(/could not be read/)).toBeInTheDocument();
    expect(screen.queryByText(/may have restarted/)).toBeNull();
  });
});

describe("REQ-BT-030 the Logs gap", () => {
  it("REQ-BT-030 starts where data stopped, not at the first read", async () => {
    const clock = vi.spyOn(Date, "now").mockReturnValue(NOW);
    installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": { body: lineLogs() } });
    renderApp("/logs");
    await screen.findByRole("table", { name: "Log entries" });
    const line = dropped("/line/ws");
    act(() => line.socket.open());
    const later = NOW + 3_600_000;
    clock.mockReturnValue(later);
    act(() => line.socket.send(lineSnapshot()));
    await line.drop();
    expect(await screen.findByText(new RegExp(`Entries after ${formatTime(later)} are missing`))).toBeInTheDocument();
  });
});

describe("REQ-BT-030 the Logs gap after an unread hole", () => {
  it("REQ-BT-030 starts at the last read, so the hole is not hidden by later frames", async () => {
    const clock = vi.spyOn(Date, "now").mockReturnValue(NOW);
    const at = new Date(NOW + 60_000).toISOString();
    installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": { body: lineLogs({ entries: [logEntry({ at: "2026-10-31T09:00:00Z" })] }) } });
    renderApp("/logs");
    await screen.findByRole("table", { name: "Log entries" });
    const line = dropped("/line/ws");
    act(() => line.socket.open());
    act(() => line.socket.send(lineSnapshot()));
    clock.mockReturnValue(NOW + 3_600_000);
    act(() => line.socket.send(lineSnapshot({ entries: [logEntry({ at, traceId: "newer" })], entriesDropped: 5 })));
    await screen.findByRole("button", { name: "Load missed entries" });
    await line.drop();
    expect(await screen.findByText(new RegExp(`Entries after ${formatTime(NOW)} are missing`))).toBeInTheDocument();
  });
});

describe("REQ-BT-053 a failure before the batch had an ID", () => {
  it.each(["http-refused", "network-failure", "unknown"])("REQ-BT-053 %s sends nobody to a batch", (reason) => {
    const guide = entryGuidance(reason, null)!;
    expect(guide.impact).toMatch(/No batch ID/);
    expect(guide.next).not.toMatch(/open the batch/);
  });

  it("REQ-BT-053 a lost reply says a batch may exist, and a refusal points at the answer", () => {
    expect(entryGuidance("network-failure", null)!.impact).toMatch(/may exist/);
    expect(entryGuidance("http-refused", null)!.next).toMatch(/API's answer/);
    expect(entryGuidance("unknown", null)!.next).toMatch(/full message/);
  });

  it("REQ-BT-053 with an ID the reason's own words stand", () => {
    expect(entryGuidance("http-refused", "b-1")!.next).toMatch(/open the batch/);
  });

  it("REQ-BT-053 a sign-in failure keeps its own words, and information has none", () => {
    expect(entryGuidance("login-failure", null)!.next).toMatch(/sign in/);
    expect(entryGuidance("started", null)).toBeNull();
  });
});

describe("REQ-BT-045 Start while the line socket is lost", () => {
  async function lostLine(running: boolean) {
    installApi({ "GET /api/me": meRoute("ada"), "GET /api/batches": { body: [] }, "POST /line": { status: 204 }, "DELETE /line": { status: 204 } });
    renderApp("/dashboard");
    await screen.findByRole("heading", { name: "Production line" }, { timeout: 5000 });
    const line = dropped("/line/ws");
    act(() => line.socket.open());
    act(() => line.socket.send(lineSnapshot({ status: { ...LINE_STOPPED, running, rate: running ? 100 : 0 } })));
    await line.drop();
    await screen.findByText(/Last known:/);
  }

  it("REQ-BT-045 says the start was accepted", async () => {
    await lostLine(false);
    await userEvent.click(screen.getByRole("button", { name: "Start line" }));
    expect(await screen.findByText(/Start accepted/)).toBeInTheDocument();
  });

  it("REQ-BT-045 after an accepted stop the button says Start line", async () => {
    await lostLine(true);
    await userEvent.click(screen.getByRole("button", { name: "Stop line" }));
    await screen.findByText(/Stop accepted/);
    expect(screen.getByRole("button", { name: "Start line" })).toBeInTheDocument();
  });

  it("REQ-BT-045 a failed start keeps the record of the stop", async () => {
    await lostLine(true);
    await userEvent.click(screen.getByRole("button", { name: "Stop line" }));
    await screen.findByText(/Stop accepted/);
    installApi({ "GET /api/me": meRoute("ada"), "POST /line": { status: 500, body: {} } });
    await userEvent.click(screen.getByRole("button", { name: "Start line" }));
    await screen.findByText("The request failed");
    expect(screen.getByText(/Stop accepted/)).toBeInTheDocument();
  });
});

describe("REQ-BT-045 an old stop", () => {
  it("REQ-BT-045 is forgotten when a frame shows the line running again", async () => {
    adminDashboard();
    await screen.findByRole("heading", { name: "Production line" }, { timeout: 5000 });
    const line = dropped("/line/ws");
    act(() => line.socket.open());
    act(() => line.socket.send(lineSnapshot({ status: { ...LINE_STOPPED, running: true, rate: 5 } })));
    await userEvent.click(await screen.findByRole("button", { name: "Stop line" }));
    await screen.findByRole("button", { name: "Stop line" });
    act(() => line.socket.send(lineSnapshot({ status: LINE_STOPPED })));
    act(() => line.socket.send(lineSnapshot({ status: { ...LINE_STOPPED, running: true, rate: 100 } })));
    await line.drop();
    expect(await screen.findByText(/Last known: running at 100 per second/)).toBeInTheDocument();
    expect(screen.queryByText(/Stop accepted/)).toBeNull();
  });
});

describe("REQ-BT-050 Recent hops with its stream lost", () => {
  it("REQ-BT-050 the empty list says the stream is lost, not to start the line", async () => {
    adminDashboard();
    const panel = await screen.findByRole("heading", { name: "Recent hops" }, { timeout: 5000 });
    const hops = dropped("/ws/hops");
    act(() => hops.socket.open());
    await hops.drop();
    const section = panel.closest("section")!;
    expect(await within(section).findByText("Hops unknown")).toBeInTheDocument();
    expect(within(section).queryByText(/Start the production line/)).toBeNull();
  });
});

describe("REQ-BT-030 Changes with a silent monitor", () => {
  it("REQ-BT-030 no changes is unknown, not none", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    adminDashboard();
    const heading = await screen.findByRole("heading", { name: "Changes" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() => socket.send(REPORT));
    vi.setSystemTime(Date.now() + MONITOR_STALE_MS + 2000);
    await act(() => vi.advanceTimersByTimeAsync(1100));
    expect(screen.queryByText("No changes yet")).toBeNull();
    expect(within(heading.closest("section")!).getByText("Unknown")).toBeInTheDocument();
  });
});
