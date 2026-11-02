import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MONITOR_WS } from "../config";
import { alert, batch, hop, hopSnapshot, lineLogs, lineSnapshot, logEntry, pending, REPORT, step } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../mocks/harness";

const NOW = new Date(2026, 9, 31, 12, 0, 0).getTime();
const TODAY = new Date(NOW - 3_600_000).toISOString();
const stat = (label: string) => [...document.querySelectorAll(".ds-stat")].find((s) => s.textContent?.includes(label))!;
const EMPTY = { byState: {}, pending: 0, blocked: 0, flagged: 0 };


beforeEach(() => {
  useFakeSockets();
  localStorage.clear();
  vi.spyOn(Date, "now").mockReturnValue(NOW);
});

afterEach(() => void vi.restoreAllMocks());

describe("REQ-BT-055 reports not read yet are never all clear", () => {
  const unread = { reconciledAt: null, summary: EMPTY, alerts: [] };

  it("REQ-BT-055 the role dashboard shows no zeros", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [] } });
    FakeSocket.greet("/ws/hops", hopSnapshot(unread));
    renderApp("/dashboard");
    expect(await screen.findByText("The live service has not read the store yet")).toBeInTheDocument();
    expect(screen.queryByRole("img", { name: /0 Created/ })).toBeNull();
  });

  it("REQ-BT-050 alerts do not say none are open", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    FakeSocket.greet("/ws/hops", hopSnapshot(unread));
    renderApp("/alerts");
    expect(await screen.findByText("The live service has not read the store yet")).toBeInTheDocument();
    expect(screen.queryByText("No open alerts")).toBeNull();
  });

  it("REQ-BT-050 until the first scan the tiles say loading, not unknown or zero", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [] } });
    FakeSocket.greet("/ws/hops", hopSnapshot(unread));
    renderApp("/dashboard");
    await screen.findByText("Blocked");
    for (const label of ["Blocked", "Rejected", "Flagged", "Event in flight"]) expect(stat(label)).toHaveTextContent("Loading");
  });

  it("REQ-BT-050 the startup state reads as loading, not as an error", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    FakeSocket.greet("/ws/hops", hopSnapshot(unread));
    renderApp("/alerts");
    const text = await screen.findByText("The live service has not read the store yet");
    expect(text.closest(".ds-error-state")).toBeNull();
    expect(text.closest(".ds-empty")).not.toBeNull();
  });

  it("REQ-BT-028 hops stay live until the first scan, and the batch frames fill in", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [] } });
    FakeSocket.greet("/ws/hops", hopSnapshot(unread));
    renderApp("/batches");
    await screen.findByRole("heading", { level: 1, name: "Batches" });
    act(() => FakeSocket.find("/ws/hops").send({ type: "batch", etag: '"v2"', batch: batch({ version: 2 }) }));
    expect(await screen.findByRole("link", { name: "Bovine vaccine" })).toBeInTheDocument();
    expect(screen.queryByText("Open")).toBeNull();
    act(() => FakeSocket.find("/ws/hops").send({ type: "summary", summary: { ...EMPTY, byState: { Dose1: 1 } }, reconciledAt: TODAY }));
    expect(await screen.findByText("Open")).toBeInTheDocument();
  });

  it("REQ-BT-028 a dot still moves on the admin map before the first scan", async () => {
    installApi({ "GET /api/me": meRoute("ada") });
    FakeSocket.greet("/ws/hops", hopSnapshot(unread));
    FakeSocket.greet("/line/ws", lineSnapshot());
    renderApp("/dashboard");
    await screen.findByRole("heading", { name: "Architecture" });
    act(() => FakeSocket.find("/ws/hops").sendHops([hop({ at: new Date(NOW).toISOString() })]));
    expect(await screen.findByRole("group", { name: /1 hop seen/ })).toBeInTheDocument();
  });

  it("REQ-BT-050 an alert frame before the first read stays unread", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    FakeSocket.greet("/ws/hops", hopSnapshot(unread));
    renderApp("/alerts");
    await screen.findByText("The live service has not read the store yet");
    act(() => FakeSocket.find("/ws/hops").send({ type: "alerts", alerts: [] }));
    await act(async () => undefined);
    expect(screen.queryByText("No open alerts")).toBeNull();
  });

  it("REQ-BT-050 the first read fills summary and alerts in", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    FakeSocket.greet("/ws/hops", hopSnapshot(unread));
    renderApp("/alerts");
    await screen.findByText("The live service has not read the store yet");
    act(() => FakeSocket.find("/ws/hops").send({ type: "summary", summary: EMPTY, reconciledAt: TODAY }));
    expect(await screen.findByText("No open alerts")).toBeInTheDocument();
  });
});

describe("REQ-BT-050 a lost feed proves nothing", () => {
  it("REQ-BT-050 no open alerts turns into unknown", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    renderApp("/alerts");
    await screen.findByText("No open alerts");
    await act(async () => FakeSocket.find("/ws/hops").drop());
    expect(await screen.findByText("Alerts are unknown")).toBeInTheDocument();
    expect(screen.queryByText("No open alerts")).toBeNull();
  });
});

describe("REQ-BT-053 alerts tell each reader what they can do", () => {
  const open = async (user: "olivia" | "ian", item = alert()) => {
    installApi({ "GET /api/me": meRoute(user) });
    FakeSocket.greet("/ws/hops", hopSnapshot({ alerts: [item] }));
    renderApp("/alerts");
    await userEvent.click(await screen.findByRole("button", { name: /details of/ }));
    return document.querySelector(".ds-error-detail")!;
  };

  it("REQ-BT-053 an overdue dose asks the writer to check it", async () => {
    expect(await open("olivia", alert({ type: "Dose2Overdue" }))).toHaveTextContent("Open the batch and check Dose 2.");
  });

  it("REQ-BT-053 an overdue dose sends anyone else to an operator", async () => {
    expect(await open("ian", alert({ type: "Dose2Overdue" }))).toHaveTextContent("Ask an operator to check Dose 2.");
  });

  it("REQ-BT-053 the raw detail of an alert is folded, not in the table", async () => {
    await open("olivia", alert({ type: "Blocked", detail: "poison" }));
    const details = document.querySelector("details")!;
    expect(details).toHaveTextContent("poison");
    expect(details).not.toHaveAttribute("open");
    expect(document.querySelector(".ds-error-detail")).not.toHaveTextContent("poison");
  });
});

describe("REQ-BT-043 health before the first checks", () => {
  it("REQ-BT-043 no checks yet is not all healthy", async () => {
    installApi({ "GET /api/me": meRoute("ada") });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    FakeSocket.greet("/line/ws", lineSnapshot());
    renderApp("/dashboard");
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() => socket.send({ ...REPORT, checkedAt: new Date(NOW).toISOString(), healthy: false, checks: [] }));
    expect(await screen.findByText("Checks have not run yet")).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("All 0 checks");
    expect(stat("Checks healthy")).toHaveTextContent("Unknown");
  });
});

describe("REQ-BT-053 batch banners", () => {
  it.each([
    ["delayed", new Date(NOW - 3_600_000).toISOString(), "Compliance record is delayed"],
    ["in flight", new Date(NOW - 1_000).toISOString(), "Event in flight"],
  ])("REQ-BT-053 %s reads in order and names a control that exists", async (_name, sentAt, title) => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches/farm-a/b-1": { etag: '"v1"', body: batch({ steps: [step("Dose1", false, { at: sentAt })], pending: pending("sent", sentAt) }) } });
    renderApp("/batches/farm-a/b-1");
    const banner = (await screen.findByText(title)).closest(".ds-banner")!;
    expect(banner.querySelector(".ds-error-detail")).not.toBeNull();
    expect(banner).not.toHaveTextContent("Refresh");
  });
});

describe("REQ-BT-053 a failed write gives one instruction", () => {
  it("REQ-BT-053 a 500 on a write says to check before repeating, in plain words", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches/farm-a/b-1": { etag: '"v1"', body: batch() }, "POST /api/batches/farm-a/b-1/advance": { status: 500 } });
    renderApp("/batches/farm-a/b-1");
    await userEvent.click(await screen.findByRole("button", { name: "Move to Dose 2" }));
    const banner = (await screen.findByText("The request failed")).closest(".ds-banner")!;
    const detail = banner.querySelector(".ds-error-detail")!;
    expect(detail).toHaveTextContent("Check the batch before repeating it.");
    expect(detail).not.toHaveTextContent("Try again.");
    expect(detail).not.toHaveTextContent("status 500");
  });
});

describe("REQ-BT-032 what a dropped socket missed is offered, never read by itself", () => {
  it("REQ-BT-032 a batch page offers a read that clears a stuck in flight", async () => {
    let row = batch({ steps: [step("Dose1", false, { at: new Date(NOW - 1000).toISOString() })], pending: pending("sent", TODAY) });
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches/farm-a/b-1": () => ({ etag: '"v1"', body: row }) });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    renderApp("/batches/farm-a/b-1");
    await screen.findByText("Event in flight");
    row = batch({ version: 2, pending: null });
    await act(async () => FakeSocket.find("/ws/hops").drop());
    const press = await screen.findByRole("button", { name: "Read again" }, { timeout: 8000 });
    expect(screen.getByText("Event in flight")).toBeInTheDocument();
    await userEvent.click(press);
    await waitFor(() => expect(screen.queryByText("Event in flight")).toBeNull());
  });

  it("REQ-BT-032 the batch list offers the read too, and makes none itself", async () => {
    let rows = [batch()];
    const calls = installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": () => ({ body: rows }) });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    renderApp("/batches");
    await screen.findByRole("link", { name: "Bovine vaccine" });
    rows = [batch({ id: "b-2", product: "New product" })];
    const before = calls.length;
    await act(async () => FakeSocket.find("/ws/hops").drop());
    const press = await screen.findByRole("button", { name: "Read again" }, { timeout: 8000 });
    expect(calls.slice(before)).toEqual([]);
    await userEvent.click(press);
    expect(await screen.findByRole("link", { name: "New product" })).toBeInTheDocument();
  });

  it("REQ-BT-045 entries that land during a re-read survive it", async () => {
    let second = false;
    let release: (() => void) | null = null;
    const empty = { body: lineLogs({ entries: [], matched: 0 }) };
    const route = () => (second ? new Promise<typeof empty>((resolve) => (release = () => resolve(empty))) : empty);
    installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": route });
    FakeSocket.greet("/line/ws", lineSnapshot());
    renderApp("/logs");
    await screen.findByText("No entries match these filters").catch(() => screen.findByRole("heading", { name: "Entries" }));
    second = true;
    await act(async () => FakeSocket.find("/line/ws").drop());
    await screen.findByText("Live updates are lost");
    await waitFor(() => expect(screen.queryByText("Live updates are lost")).toBeNull(), { timeout: 8000 });
    await userEvent.click(await screen.findByRole("button", { name: "Load missed entries" }));
    await waitFor(() => expect(release).not.toBeNull());
    const entry = logEntry({ at: new Date(NOW).toISOString(), message: "Landed during the read" });
    await act(async () => FakeSocket.find("/line/ws").send({ ...lineSnapshot(), type: "line", at: entry.at, entries: [entry] }));
    await act(async () => release!());
    expect(await screen.findByText("Landed during the read")).toBeInTheDocument();
  });
});

describe("REQ-BT-052 the correlation filter tells the truth", () => {
  const open = (path: string, entries = [logEntry({ correlationId: "corr-9" }), logEntry({ at: "2026-10-31T10:01:00.000Z", message: "Other entry", correlationId: "corr-1" }), logEntry({ at: "2026-10-31T10:02:00.000Z", message: "No ID entry" })]) => {
    const calls = installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": { body: lineLogs({ entries, matched: entries.length, byReason: { "event-timeout": entries.length } }) } });
    FakeSocket.greet("/line/ws", lineSnapshot());
    renderApp(path);
    return calls;
  };

  it("REQ-BT-052 the ID goes to the Line, which does the filtering", async () => {
    const calls = open("/logs?correlationId=corr-9", [logEntry({ correlationId: "corr-9" })]);
    await screen.findByText(logEntry().message);
    expect(calls.find((c) => c.url.includes("/line/logs"))!.url).toContain("correlationId=corr-9");
  });

  it("REQ-BT-052 a pushed frame adds only rows with that ID to what the read answered", async () => {
    open("/logs?correlationId=corr-9", [logEntry({ correlationId: "corr-9", message: "Answered row" })]);
    await screen.findByText("Answered row");
    const pushed = [logEntry({ at: "2026-10-31T10:02:00.000Z", message: "Other entry", correlationId: "corr-1" }), logEntry({ at: "2026-10-31T10:03:00.000Z", message: "Pushed row", correlationId: "corr-9" })];
    await act(async () => FakeSocket.find("/line/ws").send({ ...lineSnapshot(), type: "line", at: "2026-10-31T10:03:00.000Z", entries: pushed }));
    expect(await screen.findByText("Pushed row")).toBeInTheDocument();
    expect(screen.getByText("Answered row")).toBeInTheDocument();
    expect(screen.queryByText("Other entry")).toBeNull();
  });

  it("REQ-BT-052 an ID nothing carries says nothing matches", async () => {
    open("/logs?correlationId=nobody", []);
    expect(await screen.findByText("No entries match these filters")).toBeInTheDocument();
  });

  it("REQ-BT-045 after the line feed comes back the list offers one read, which shows the truth", async () => {
    let entries = [logEntry({ message: "Before the restart" })];
    const route = () => ({ body: lineLogs({ entries, matched: entries.length }) });
    installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": route });
    FakeSocket.greet("/line/ws", lineSnapshot());
    renderApp("/logs");
    await screen.findByText("Before the restart");
    entries = [];
    await act(async () => FakeSocket.find("/line/ws").drop());
    await userEvent.click(await screen.findByRole("button", { name: "Load missed entries" }, { timeout: 8000 }));
    await waitFor(() => expect(screen.queryByText("Before the restart")).toBeNull());
  });

  it("REQ-BT-052 counts follow the filter, not the whole line", async () => {
    open("/logs?level=error&correlationId=corr-9");
    await screen.findByText(logEntry().message);
    expect(screen.queryByText("Since the line started.", { exact: false })).toBeNull();
  });
});
