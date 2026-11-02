import { act, renderHook, screen, waitFor, within } from "@testing-library/react";
import { useEffect } from "react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Batch, LineLogs } from "../api/types";
import { LOG_LIMIT } from "../config";
import { mergeLogs } from "../logic/feed/live";
import { counterNote, logGuidance } from "../logic/monitor/logs";
import { etaText } from "../logic/monitor/operations";
import { useMissedRead } from "../live/useResource";
import { batch, hopSnapshot, lineLogs, lineSnapshot, logEntry, pending, step, USERS } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../mocks/harness";

const NOW = new Date(2026, 9, 31, 12, 0, 0).getTime();
const TODAY = new Date(NOW - 3_600_000).toISOString();
const NO_FILTERS = { level: "", reason: "", farm: "", batch: "", correlationId: "", from: "", to: "" };
const stat = (label: string) => [...document.querySelectorAll(".ds-stat")].find((s) => s.textContent?.includes(label))!;
const inFlight = (over: Partial<Batch> = {}) => batch({ steps: [step("Dose1", false, { at: new Date(NOW - 1000).toISOString() })], pending: pending("sent", TODAY), ...over });

beforeEach(() => {
  useFakeSockets();
  localStorage.clear();
  vi.spyOn(Date, "now").mockReturnValue(NOW);
});
afterEach(() => vi.restoreAllMocks());

describe("REQ-BT-050 a session that ended while the socket was away", () => {
  it("REQ-BT-050 Reconnect now finds out, and the page goes to sign in", async () => {
    let ended = false;
    installApi({ "GET /api/me": () => (ended ? { status: 401 } : { body: USERS.olivia }) });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    renderApp("/alerts");
    await screen.findByRole("heading", { level: 1, name: "Alerts" });
    ended = true;
    await act(async () => FakeSocket.find("/ws/hops").drop());
    await userEvent.click(await screen.findAllByRole("button", { name: "Reconnect now" }).then((found) => found[0]!));
    expect(await screen.findByRole("heading", { name: "Sign in to Batch Tracker" })).toBeInTheDocument();
  });
});

describe("REQ-BT-053 the batch page while its socket is lost", () => {
  async function lostPage() {
    let row = inFlight();
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches/farm-a/b-1": () => ({ etag: '"v1"', body: row }) });
    renderApp("/batches/farm-a/b-1");
    await screen.findByText("Event in flight");
    const socket = FakeSocket.find("/ws/hops");
    act(() => socket.open());
    act(() => socket.send(hopSnapshot()));
    // No greeting, so the socket that retries never opens and the page stays lost.
    await act(async () => socket.drop());
    return (next: Batch) => (row = next);
  }

  it("REQ-BT-053 it no longer says it updates by itself", async () => {
    await lostPage();
    await screen.findAllByText(/connection lost/i);
    expect(screen.queryByText(/updates by itself/)).toBeNull();
    expect(screen.getByText(/may be behind/)).toBeInTheDocument();
  });

  it("REQ-BT-032 the one read is offered at a press while the socket stays lost", async () => {
    const answer = await lostPage();
    answer(batch({ version: 2, pending: null }));
    await userEvent.click(await screen.findByRole("button", { name: "Read again" }));
    await waitFor(() => expect(screen.queryByText("Event in flight")).toBeNull());
  });
});

describe("REQ-BT-032 the missed read offer", () => {
  const resource = (data: unknown) => ({ reload: () => undefined, data });

  it("REQ-BT-032 stands while lost and after the return, and a read from anywhere clears it", () => {
    const view = renderHook(({ status, data }) => useMissedRead(status, resource(data)), { initialProps: { status: "open", data: 1 } });
    expect(view.result.current.missed).toBe(false);
    view.rerender({ status: "lost", data: 1 });
    expect(view.result.current.missed).toBe(true);
    view.rerender({ status: "open", data: 1 });
    expect(view.result.current.missed).toBe(true);
    view.rerender({ status: "open", data: 2 });
    expect(view.result.current.missed).toBe(false);
  });
});

describe("REQ-BT-032 the offer through the return", () => {
  it("REQ-BT-032 never leaves the screen for a commit, so a press on it cannot be lost", () => {
    const committed: boolean[] = [];
    const reload = () => undefined;
    const view = renderHook(
      ({ status }) => {
        const read = useMissedRead(status, { reload, data: 1 });
        useEffect(() => void committed.push(read.missed));
        return read;
      },
      { initialProps: { status: "lost" } },
    );
    view.rerender({ status: "open" });
    expect(committed).not.toContain(false);
  });
});

describe("REQ-BT-054 an estimate of nothing", () => {
  it("REQ-BT-054 zero from steps with no earlier run says none exists", () => {
    const at = new Date(NOW - 5000).toISOString();
    const operation = { id: "one", name: "Deploy", step: "Charts", startedAt: at, lastSeenAt: at, state: "running" as const, endedAt: null, steps: [], etaSeconds: 0 };
    expect(etaText(operation, NOW)).toBe("No estimate yet");
  });
});

describe("REQ-BT-053 a log error says what it did to the batch and what to do", () => {
  it.each(["event-timeout", "http-refused", "network-failure", "login-failure", "unknown"])("REQ-BT-053 %s has both", (reason) => {
    expect(logGuidance(reason)?.impact).toBeTruthy();
    expect(logGuidance(reason)?.next).toBeTruthy();
  });

  it("REQ-BT-053 an event timeout says the batch is not failed", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": { body: lineLogs() } });
    FakeSocket.greet("/line/ws", lineSnapshot());
    renderApp("/logs?level=error");
    const table = await screen.findByRole("table", { name: "Log entries" });
    await userEvent.click(within(table).getAllByRole("button")[0]!);
    expect(await screen.findByText(/not failed/)).toBeInTheDocument();
  });

  it("REQ-BT-053 information entries carry none", () => {
    expect(logGuidance("closed")).toBeNull();
  });
});

describe("REQ-BT-045 merging into a full list", () => {
  const entry = (at: number, message: string) => logEntry({ at: new Date(at).toISOString(), message, traceId: message });
  const full: LineLogs = lineLogs({ matched: LOG_LIMIT + 50, droppedByLimit: 50, byReason: { "event-timeout": LOG_LIMIT + 50 }, entries: Array.from({ length: LOG_LIMIT }, (_, i) => entry(NOW - i * 1000, `row ${i}`)) });
  const frame = (entries: ReturnType<typeof entry>[]) => ({ type: "line" as const, at: new Date(NOW).toISOString(), status: null, entries, entriesDropped: 0, evicted: 0 });

  it("REQ-BT-045 an entry older than a full list is not news", () => {
    const merged = mergeLogs(full, frame([entry(NOW - 900_000, "from long ago")]) as never, NO_FILTERS, LOG_LIMIT);
    expect(merged.entries[0]!.message).toBe("row 0");
    expect(merged.matched).toBe(full.matched);
  });

  it("REQ-BT-045 a newer one still lands on top", () => {
    const merged = mergeLogs(full, frame([entry(NOW + 1000, "just now")]) as never, NO_FILTERS, LOG_LIMIT);
    expect(merged.entries[0]!.message).toBe("just now");
    expect(merged.matched).toBe(full.matched + 1);
  });
});

describe("REQ-BT-045 the Dashboard counter after a restart", () => {
  it("REQ-BT-045 fewer counted than listed names the likely cause", () => {
    const note = counterNote(0, lineLogs({ matched: 57 }), { ...NO_FILTERS, level: "error" });
    expect(note.text).toMatch(/restart/);
    expect(note.text).not.toMatch(/a moment apart/);
  });
});

describe("REQ-BT-053 a refusal made in the browser", () => {
  it("REQ-BT-053 says nothing was saved and claims no server answer", async () => {
    let reads = 0;
    installApi({
      "GET /api/me": meRoute("ada"),
      "GET /api/batches/farm-a/b-1": () => ({ etag: '"v1"', body: ++reads === 1 ? batch() : batch({ version: 2, state: "Closed" }) }),
    });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    renderApp("/batches/farm-a/b-1");
    await userEvent.click(await screen.findByRole("button", { name: "Fail next delivery" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Fail next delivery" }));
    expect(await screen.findByText("Nothing was saved.")).toBeInTheDocument();
    expect(screen.queryByText("Server answer")).toBeNull();
  });
});

describe("REQ-BT-050 tiles tell the states apart", () => {
  const dashboard = async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [] } });
    renderApp("/dashboard");
    await screen.findByText("Batches by step", {}, { timeout: 5000 });
  };

  it("REQ-BT-050 before the first frame they read Loading", async () => {
    await dashboard();
    expect(stat("Blocked")).toHaveTextContent("Loading");
  });

  it("REQ-BT-050 lost during the first scan they read Unknown, and promise nothing", async () => {
    await dashboard();
    const socket = FakeSocket.find("/ws/hops");
    act(() => socket.open());
    act(() => socket.send(hopSnapshot({ reconciledAt: null })));
    await act(async () => socket.drop());
    expect(stat("Blocked")).toHaveTextContent("Unknown");
    expect(screen.queryByText("Totals appear here as soon as it has.")).toBeNull();
  });
});

describe("REQ-BT-050 a batch shown from the socket", () => {
  it("REQ-BT-050 never reads Updated never", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches/farm-a/b-1": { status: 500, body: {} } });
    FakeSocket.greet("/ws/hops", hopSnapshot({ batches: [{ etag: '"v1"', batch: batch() }], at: new Date(NOW).toISOString() }));
    renderApp("/batches/farm-a/b-1");
    await screen.findByRole("heading", { level: 1, name: batch().product });
    await waitFor(() => expect(screen.getByText(/^Updated /)).toBeInTheDocument());
    expect(screen.queryByText(/^Updated never/)).toBeNull();
  });
});
