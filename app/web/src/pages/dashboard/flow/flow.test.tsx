import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { MONITOR_STALE_MS, MONITOR_WS } from "../../../config";
import type { Batch } from "../../../api/types";
import { REPORT, batch, hop, hopSnapshot, lineSnapshot } from "../../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Handler, type Reply } from "../../../mocks/harness";

function dashboard(extra: Record<string, Reply | Handler> = {}, rows: Batch[] = [batch()], line = true) {
  const calls = installApi({ "GET /api/me": meRoute("ada"), ...extra });
  if (line) FakeSocket.greet("/line/ws", lineSnapshot());
  FakeSocket.greet("/ws/hops", hopSnapshot({ batches: rows.map((row) => ({ etag: '"v1"', batch: row })) }));
  renderApp("/dashboard");
  return calls;
}

function nextFrame() {
  return act(() => new Promise<void>((resolve) => setTimeout(resolve, 320)));
}

beforeEach(() => useFakeSockets());

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("live diagram", () => {
  it("REQ-BT-028 real hops count at their stations and show in text", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ hop: "api" }), hop({ hop: "bus" }), hop({ hop: "worker", ok: false, traceId: "t2" })]));
    await nextFrame();
    expect(screen.getByRole("group", { name: /3 hops seen, 1 failed/ })).toBeInTheDocument();
    expect(document.querySelector('[data-station="worker"]')).toHaveTextContent("1 read, 1 failed");
    expect(within(screen.getByRole("table", { name: "Recent hops" })).getByText("Failed")).toBeInTheDocument();
  });

  it("REQ-BT-028 500 hops in a tick stay responsive and report undrawn traces", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops(Array.from({ length: 500 }, (_, i) => hop({ traceId: `t${i}` }))));
    await nextFrame();
    expect(screen.getByText(/more steps not drawn/)).toBeInTheDocument();
    expect(within(screen.getByRole("table", { name: "Recent hops" })).getAllByRole("row")).toHaveLength(13);
  });

  it("REQ-BT-036 no hops yet is designed", async () => {
    dashboard();
    expect(await screen.findByText("No hops yet")).toBeInTheDocument();
  });

  it("REQ-BT-028 a trace can be followed and released", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ traceId: "aaa" }), hop({ traceId: "bbb" })]));
    await nextFrame();
    expect(screen.getByRole("button", { name: "Follow trace aaa" })).not.toHaveAttribute("aria-label");
    await userEvent.click(screen.getByRole("button", { name: "Follow trace aaa" }));
    expect(within(screen.getByRole("table", { name: "Recent hops" })).getAllByRole("row")).toHaveLength(2);
    expect(screen.getByText(/Following trace aaa/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Show all traces" }));
    expect(within(screen.getByRole("table", { name: "Recent hops" })).getAllByRole("row")).toHaveLength(3);
  });

  it("REQ-BT-028 freeze holds the list still so a row can be pressed", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ traceId: "aaa" })]));
    await nextFrame();
    await userEvent.click(screen.getByRole("button", { name: "Freeze list" }));
    act(() => socket.sendHops([hop({ traceId: "bbb" })]));
    await nextFrame();
    expect(within(screen.getByRole("table", { name: "Recent hops" })).getAllByRole("row")).toHaveLength(2);
  });
});

describe("recent hops notes", () => {
  it("REQ-BT-028 a frozen list says when it froze, and Resume list removes the note", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ traceId: "aaa" })]));
    await nextFrame();
    expect(screen.queryByText(/List frozen at/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Freeze list" }));
    expect(screen.getByText(/List frozen at \d/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Resume list" }));
    expect(screen.queryByText(/List frozen at/)).not.toBeInTheDocument();
  });

  it("REQ-BT-028 a list longer than the rows shown says how many it holds", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops(Array.from({ length: 14 }, (_, i) => hop({ traceId: `t${i}` }))));
    await nextFrame();
    expect(screen.getByText("Showing the newest 12 of 14 hops.")).toBeInTheDocument();
  });
});

describe("reconnect history", () => {
  it("REQ-BT-028 the hub resending its last hops on reconnect does not count them twice", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const first = FakeSocket.find("/ws/hops");
    first.open();
    const history = [hop({ traceId: "a" }), hop({ traceId: "b" })];
    act(() => first.send(hopSnapshot({ hops: history })));
    await nextFrame();
    expect(screen.getByRole("group", { name: /2 hops seen/ })).toBeInTheDocument();
    act(() => first.drop());
    await waitFor(() => expect(FakeSocket.all.filter((s) => s.url.includes("/ws/hops")).length).toBeGreaterThan(1), { timeout: 2500 });
    const second = FakeSocket.all.filter((s) => s.url.includes("/ws/hops")).at(-1)!;
    second.open();
    act(() => second.send(hopSnapshot({ hops: history })));
    await nextFrame();
    expect(screen.getByRole("group", { name: /2 hops seen/ })).toBeInTheDocument();
  });

  it("REQ-BT-028 old hops in a history frame go to the list but draw no dots", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ traceId: "old", at: "2026-10-30T09:00:00.000Z" }), hop({ traceId: "new", at: "2026-10-30T10:00:00.000Z" })]));
    await nextFrame();
    expect(screen.getByText(/2 hops seen since .* in this view, 1 dot on screen/)).toBeInTheDocument();
  });
});

describe("realtime and playback", () => {
  it("REQ-BT-029 RealTime Monitor starts checked and speed is locked", async () => {
    dashboard();
    expect(await screen.findByRole("checkbox", { name: "RealTime Monitor" })).toBeChecked();
    for (const speed of ["0.25x", "0.5x", "1x", "2x"]) expect(screen.getByRole("radio", { name: speed })).toBeDisabled();
  });

  it("REQ-BT-029 off replays buffered hops; speed control works; on returns to live", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ traceId: "a" }), hop({ traceId: "b", at: "2026-10-30T10:00:00.100Z" })]));
    await nextFrame();
    await userEvent.click(screen.getByRole("checkbox", { name: "RealTime Monitor" }));
    expect(screen.getByRole("radio", { name: "2x" })).toBeEnabled();
    await userEvent.click(screen.getByRole("radio", { name: "2x" }));
    expect(screen.getByRole("radio", { name: "2x" })).toBeChecked();
    await nextFrame();
    await nextFrame();
    expect(screen.getByText(/Playback at 2x/)).toBeInTheDocument();
    expect(screen.getByRole("group", { name: /2 hops seen/ })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("checkbox", { name: "RealTime Monitor" }));
    expect(screen.getByRole("radio", { name: "2x" })).toBeDisabled();
    expect(screen.queryByText(/Playback at/)).toBeNull();
  });

  it("REQ-BT-029 playback with an empty buffer says so", async () => {
    dashboard();
    await userEvent.click(await screen.findByRole("checkbox", { name: "RealTime Monitor" }));
    expect(await screen.findByText(/no hops buffered yet/)).toBeInTheDocument();
  });

  it("REQ-BT-029 hops arriving during playback wait in the buffer, not on screen", async () => {
    dashboard();
    await userEvent.click(await screen.findByRole("checkbox", { name: "RealTime Monitor" }));
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    await userEvent.click(screen.getByRole("radio", { name: "0.25x" }));
    act(() => socket.sendHops([hop({ traceId: "x" }), hop({ traceId: "y", at: "2026-10-30T10:00:30.000Z" }), hop({ traceId: "z", at: "2026-10-30T10:00:31.000Z" })]));
    await nextFrame();
    await waitFor(() => expect(screen.getByText(/hops? still to play/)).toBeInTheDocument());
    expect(screen.getByRole("radio", { name: "0.25x" })).toBeChecked();
  });
});

describe("lost sockets", () => {
  it("REQ-BT-030 hop stream lost says so on the diagram", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop()]));
    act(() => socket.drop());
    expect(await screen.findByText("Dots stopped: the hop stream is lost")).toBeInTheDocument();
    expect(screen.getByText(/Hop stream: connection lost/)).toBeInTheDocument();
    expect(document.querySelector("figure")).toHaveAttribute("data-lost", "true");
  });

  it("REQ-BT-030 Recent hops are tiles, and stay a table to a screen reader", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ hop: "api" })]));
    await nextFrame();
    expect(screen.getByRole("table", { name: "Recent hops" })).toHaveAttribute("data-layout", "tiles");
  });

  it("REQ-BT-030 Health is tiles, and a check with nothing to say shows no Detail row", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() => socket.send(REPORT));
    const table = await screen.findByRole("table", { name: "Health checks" });
    expect(table).toHaveAttribute("data-layout", "tiles");
    expect(within(table).queryByText("None")).toBeNull();
  });

  it("REQ-BT-030 monitor lost turns every green into last known", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() => socket.send(REPORT));
    expect(await screen.findByText("All 2 checks healthy")).toBeInTheDocument();
    expect(within(screen.getByRole("table", { name: "Health checks" })).getAllByText("Healthy")).toHaveLength(2);
    act(() => socket.drop());
    expect(await screen.findByText(/Monitor: connection lost/)).toBeInTheDocument();
    expect(screen.queryByText("All 2 checks healthy")).toBeNull();
    expect(screen.getByText("Unknown. Showing the last report.")).toBeInTheDocument();
    expect(within(screen.getByRole("table", { name: "Health checks" })).queryByText("Healthy")).toBeNull();
    expect(within(screen.getByRole("table", { name: "Health checks" })).getAllByText("Was healthy")).toHaveLength(2);
    expect(within(screen.getByRole("table", { name: "Nodes" })).getAllByText("Was ready")).toHaveLength(2);
    expect(document.querySelector('[data-station="cosmos"]')).toHaveTextContent("Stale");
  });

  it("REQ-BT-030 an open socket that goes silent stops reading green", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() => socket.send(REPORT));
    expect(await screen.findByText("All 2 checks healthy")).toBeInTheDocument();
    vi.setSystemTime(Date.now() + MONITOR_STALE_MS + 2000);
    await act(() => vi.advanceTimersByTimeAsync(1100));
    expect(screen.queryByText("All 2 checks healthy")).toBeNull();
    expect(screen.getByText(/Monitor: data stale/)).toBeInTheDocument();
  });

  it("REQ-BT-030 monitor never reached shows an error with a way out", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    act(() => FakeSocket.exact(MONITOR_WS).drop());
    expect(await screen.findByText("No report from the monitor")).toBeInTheDocument();
  });
});
