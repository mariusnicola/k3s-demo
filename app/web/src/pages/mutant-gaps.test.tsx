import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { LINE_STOPPED, batch, hop, hopSnapshot, lineLogs, lineSnapshot, logEntry } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../mocks/harness";

const stat = (label: string) => [...document.querySelectorAll(".ds-stat")].find((s) => s.textContent?.includes(label))!;
const EMPTY = { byState: {}, pending: 0, blocked: 0, flagged: 0 };

beforeEach(() => useFakeSockets());
afterEach(() => vi.restoreAllMocks());

describe("REQ-BT-050 a read while the socket is lost", () => {
  it("REQ-BT-050 is not undone by a frozen row", async () => {
    const moved = batch({ id: "moved", state: "Dose1", product: "Moved on" });
    let rows = [moved];
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": () => ({ body: rows }) });
    renderApp("/batches?state=Dose1");
    await screen.findByRole("link", { name: "Moved on" });
    const socket = FakeSocket.find("/ws/hops");
    act(() => socket.open());
    act(() => socket.send(hopSnapshot({ batches: [{ etag: '"v9"', batch: { ...moved, version: 9 } }] })));
    await act(async () => socket.drop());
    rows = [];
    await userEvent.click(await screen.findByRole("button", { name: "Read again" }));
    await waitFor(() => expect(screen.queryByRole("link", { name: "Moved on" })).toBeNull());
  });
});

describe("REQ-BT-045 Logs under load", () => {
  it("REQ-BT-045 the page tells the note about skipped entries", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": { body: lineLogs({ matched: 1, evicted: 9 }) } });
    renderApp("/logs?level=error");
    await screen.findByRole("table", { name: "Log entries" });
    const line = FakeSocket.find("/line/ws");
    act(() => line.open());
    act(() => line.send({ type: "line", at: "2026-10-31T11:00:00Z", status: { ...LINE_STOPPED, errors: 3 }, entries: [], entriesDropped: 5, evicted: 9 }));
    expect(await screen.findByText(/under load/)).toBeInTheDocument();
    expect(screen.queryByText(/left memory, so/)).toBeNull();
  });

  it("REQ-BT-053 an entry with no batch ID opens to the no batch words", async () => {
    const entry = logEntry({ reason: "http-refused", batchId: null });
    installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": { body: lineLogs({ entries: [entry], byReason: { "http-refused": 1 } }) } });
    FakeSocket.greet("/line/ws", lineSnapshot());
    renderApp("/logs?level=error");
    const table = await screen.findByRole("table", { name: "Log entries" });
    await userEvent.click(within(table).getAllByRole("button")[0]!);
    expect(await screen.findByText(/nothing to open/)).toBeInTheDocument();
  });
});

describe("REQ-BT-045 a start with the socket open", () => {
  it("REQ-BT-045 waits for the frame instead of guessing the state", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /api/batches": { body: [] }, "POST /line": { status: 204 } });
    FakeSocket.greet("/line/ws", lineSnapshot());
    renderApp("/dashboard");
    await userEvent.click(await screen.findByRole("button", { name: "Start line" }, { timeout: 5000 }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Start line" })).toBeEnabled());
    expect(screen.queryByText(/Idle: rate is 0/)).toBeNull();
    expect(screen.getByText("Stopped")).toBeInTheDocument();
  });
});

describe("REQ-BT-050 Recent hops that stopped", () => {
  it("REQ-BT-050 the rows left on screen say the stream is lost", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /api/batches": { body: [] } });
    renderApp("/dashboard");
    await screen.findByRole("heading", { name: "Recent hops" }, { timeout: 5000 });
    const socket = FakeSocket.find("/ws/hops");
    act(() => socket.open());
    act(() => socket.send(hopSnapshot()));
    act(() => socket.sendHops([hop()]));
    await screen.findByRole("table", { name: "Recent hops" });
    await act(async () => socket.drop());
    expect(await screen.findByText(/this list stops here/)).toBeInTheDocument();
  });
});

describe("REQ-BT-050 zeros from a store not yet read", () => {
  it("REQ-BT-050 stay Unknown when the socket is lost", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [] } });
    renderApp("/dashboard");
    await screen.findByText("Batches by step", {}, { timeout: 5000 });
    const socket = FakeSocket.find("/ws/hops");
    act(() => socket.open());
    act(() => socket.send(hopSnapshot({ reconciledAt: null, summary: EMPTY, alerts: [] })));
    await act(async () => socket.drop());
    expect(stat("Blocked")).toHaveTextContent("Unknown");
  });
});
