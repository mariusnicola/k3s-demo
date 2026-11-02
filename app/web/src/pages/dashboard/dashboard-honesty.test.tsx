import { act, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import type { Batch } from "../../api/types";
import { MONITOR_WS } from "../../config";
import { LINE_STOPPED, REPORT, batch, hop, hopSnapshot, lineSnapshot } from "../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Handler, type Reply } from "../../mocks/harness";

const ROUTE = "GET /api/batches/farm-a/b-1";

function dashboard(extra: Record<string, Reply | Handler> = {}, status = LINE_STOPPED, rows: Batch[] = [batch()]) {
  const calls = installApi({ "GET /api/me": meRoute("ada"), ...extra });
  FakeSocket.greet("/line/ws", lineSnapshot({ status }));
  FakeSocket.greet("/ws/hops", hopSnapshot({ batches: rows.map((row) => ({ etag: '"v1"', batch: row })) }));
  renderApp("/dashboard");
  return calls;
}

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

describe("dashboard tells the truth about its panels", () => {
  it("REQ-BT-028 following a trace says the counts still cover all traces", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ traceId: "trace-abc" })]));
    await userEvent.click(await screen.findByRole("button", { name: "Follow trace trace-abc" }));
    expect(screen.getByText(/counts still cover all traces/)).toBeInTheDocument();
  });

  it("REQ-BT-036 no failure target says where it looked and what brings one", async () => {
    dashboard({}, LINE_STOPPED, []);
    expect(await screen.findByText(/among the newest 50/)).toHaveTextContent("Create one or start the line");
  });

  it("REQ-BT-031 with the line socket lost a stopped line reads last known and Stop stays usable", async () => {
    dashboard();
    await screen.findByText("Stopped");
    act(() => FakeSocket.find("/line/ws").drop());
    expect(await screen.findByText(/Last known: stopped/)).toBeInTheDocument();
    expect(screen.queryByText("Stopped")).toBeNull();
    expect(screen.getByRole("button", { name: "Stop line" })).not.toHaveAttribute("aria-disabled");
  });

  it("REQ-BT-031 a line running at rate 0 reads idle", async () => {
    dashboard({}, { ...LINE_STOPPED, running: true, rate: 0 });
    expect(await screen.findByText("Idle: rate is 0")).toBeInTheDocument();
  });

  it("REQ-BT-031 the spinner sits on Stop while stopping", async () => {
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => (release = resolve));
    dashboard(
      {
        "DELETE /line": async () => {
          await gate;
          return { body: LINE_STOPPED };
        },
      },
      { ...LINE_STOPPED, running: true, rate: 1 },
    );
    const stop = await screen.findByRole("button", { name: "Stop line" });
    await userEvent.click(stop);
    expect(stop).toHaveAttribute("aria-busy", "true");
    expect(screen.getByRole("slider", { name: "Requested rate" })).toBeDisabled();
    await act(async () => release());
  });

  it("REQ-BT-030 an open monitor socket before its first report does not read stale", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    FakeSocket.exact(MONITOR_WS).open();
    expect(await screen.findByText("Monitor: live")).toBeInTheDocument();
    expect(screen.queryByText("Monitor: data stale")).toBeNull();
  });

  it("REQ-BT-028 an empty frozen list tells the operator how to resume", async () => {
    dashboard();
    await userEvent.click(await screen.findByRole("button", { name: "Freeze list" }));
    expect(await screen.findByText("List frozen with no hops")).toBeInTheDocument();
  });

  it("REQ-BT-035 focus on a row link holds the list and says so", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ batchId: "b-keep" })]));
    const link = await screen.findByRole("link", { name: "b-keep" });
    act(() => link.focus());
    expect(await screen.findByText("List held while the pointer or focus is inside it.")).toBeInTheDocument();
    act(() => socket.sendHops(Array.from({ length: 30 }, (_, i) => hop({ traceId: `t${i}` }))));
    await act(() => new Promise<void>((resolve) => setTimeout(resolve, 320)));
    expect(document.activeElement).toBe(link);
  });

  it("REQ-BT-028 a followed trace keeps its rows however many other hops arrive", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ traceId: "trace-abc", batchId: "b-mine" })]));
    await userEvent.click(await screen.findByRole("button", { name: "Follow trace trace-abc" }));
    act(() => socket.sendHops(Array.from({ length: 400 }, (_, i) => hop({ traceId: `t${i}`, batchId: `b${i}` }))));
    await act(() => new Promise<void>((resolve) => setTimeout(resolve, 320)));
    expect(screen.getByRole("link", { name: "b-mine" })).toBeInTheDocument();
  });

  it("REQ-BT-028 a trace pressed in a held list keeps its row though it left the kept window", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ traceId: "trace-old", batchId: "b-old" })]));
    const button = await screen.findByRole("button", { name: "Follow trace trace-old" });
    act(() => button.focus());
    act(() => socket.sendHops(Array.from({ length: 300 }, (_, i) => hop({ traceId: `t${i}` }))));
    await act(() => new Promise<void>((resolve) => setTimeout(resolve, 320)));
    await userEvent.click(button);
    expect(await screen.findByRole("link", { name: "b-old" })).toBeInTheDocument();
  });

  it("REQ-BT-028 following stays live after the pointer leaves the table", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ traceId: "trace-abc" })]));
    const button = await screen.findByRole("button", { name: "Follow trace trace-abc" });
    await userEvent.click(button);
    await userEvent.unhover(button);
    expect(screen.queryByText(/List held while/)).toBeNull();
  });

  it("REQ-BT-028 moving and undrawn counts fall when the dots end", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ traceId: "ending" })]));
    await screen.findByText(/1 dot on screen/);
    await screen.findByText(/, 0 dots on screen/, undefined, { timeout: 12000 });
  }, 15000);

  it("REQ-BT-028 pressing a trace releases the hold, so its new hops list", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ traceId: "trace-abc" })]));
    await userEvent.click(await screen.findByRole("button", { name: "Follow trace trace-abc" }));
    expect(screen.queryByText("List held while the pointer or focus is inside it.")).toBeNull();
    act(() => socket.sendHops([hop({ traceId: "trace-abc", hop: "worker" })]));
    await act(() => new Promise<void>((resolve) => setTimeout(resolve, 320)));
    expect(within(screen.getByRole("table", { name: "Recent hops" })).getAllByRole("row")).toHaveLength(3);
  });

  it("REQ-BT-035 leaving the window keeps the hold; leaving the table ends it", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ batchId: "b-keep" })]));
    const link = await screen.findByRole("link", { name: "b-keep" });
    act(() => link.focus());
    const away = vi.spyOn(document, "hasFocus").mockReturnValue(false);
    act(() => link.blur());
    expect(screen.getByText("List held while the pointer or focus is inside it.")).toBeInTheDocument();
    away.mockReturnValue(true);
    act(() => link.focus());
    act(() => link.blur());
    expect(screen.queryByText("List held while the pointer or focus is inside it.")).toBeNull();
    away.mockRestore();
  });

  it("REQ-BT-035 recent hops use the same state names as every other screen", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ state: "Dose1" })]));
    expect(await within(await screen.findByRole("table", { name: "Recent hops" })).findByText("Dose 1")).toBeInTheDocument();
  });

  it("REQ-BT-035 a focused trace button keeps its row while hops arrive", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop({ traceId: "trace-keep" })]));
    const button = await screen.findByRole("button", { name: "Follow trace trace-keep" });
    act(() => button.focus());
    act(() => socket.sendHops(Array.from({ length: 30 }, (_, i) => hop({ traceId: `t${i}` }))));
    await act(() => new Promise<void>((resolve) => setTimeout(resolve, 320)));
    expect(button.isConnected).toBe(true);
    expect(document.activeElement).toBe(button);
  });

  it("REQ-BT-031 the rate lever starts at the rate the line runs at", async () => {
    dashboard({}, { ...LINE_STOPPED, running: true, rate: 3 });
    expect(await screen.findByRole("slider", { name: "Requested rate" })).toHaveValue("3");
  });

  it("REQ-BT-013 nightly reports how many it flagged and links to Alerts", async () => {
    dashboard({ "POST /api/compliance/run": { body: { flagged: 2 } } });
    await userEvent.click(await screen.findByRole("button", { name: "Run nightly now" }));
    expect(await screen.findByText(/2 newly flagged/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open Alerts" })).toHaveAttribute("href", "/alerts");
  });

  it("REQ-BT-013 nightly with nothing to flag says so", async () => {
    dashboard({ "POST /api/compliance/run": { body: { flagged: 0 } } });
    await userEvent.click(await screen.findByRole("button", { name: "Run nightly now" }));
    expect(await screen.findByText("No batch needed a new flag.")).toBeInTheDocument();
  });

  it("REQ-BT-011 the confirmation names the batch it will break", async () => {
    dashboard();
    await userEvent.click(await screen.findByRole("button", { name: "Fail next delivery" }));
    expect(screen.getByRole("dialog")).toHaveTextContent("Bovine vaccine, farm-a, b-1");
  });

  it("REQ-BT-011 a batch that went final since the list loaded is refused here", async () => {
    const calls = dashboard({ [ROUTE]: { body: batch({ state: "Closed" }) } });
    await userEvent.click(await screen.findByRole("button", { name: "Fail next delivery" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Fail next delivery" }));
    expect(await screen.findByText(/can no longer take a failure/)).toBeInTheDocument();
    expect(calls.some((c) => c.url.endsWith("/fail-next"))).toBe(false);
  });

  it("REQ-BT-030 an open socket repeating an old report reads unknown", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() => socket.send({ ...REPORT, checkedAt: "2020-01-01T00:00:00Z" }));
    expect(await screen.findByText("Unknown. Showing the last report.")).toBeInTheDocument();
    expect(screen.queryByText("All 2 checks healthy")).toBeNull();
  });

  it("REQ-BT-036 with the monitor down at load, cluster panels read unknown", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    act(() => FakeSocket.exact(MONITOR_WS).drop());
    expect(await screen.findAllByText("Unknown")).toHaveLength(6);
    expect(screen.queryByRole("status", { name: "Waiting for the first monitor report" })).toBeNull();
  });

  it("REQ-BT-030 a pod phase alone is never green", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() => socket.send({ ...REPORT, checkedAt: new Date().toISOString() }));
    const badge = await within(await screen.findByRole("table", { name: "Pods" })).findByText("Running");
    expect(badge.closest(".ds-badge")).toHaveAttribute("data-tone", "neutral");
  });

  it("REQ-BT-028 leaving realtime does not claim no hop ever arrived", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.sendHops([hop()]));
    // Playback fills the list on its first frame, so with frames running this text can be gone before it is read.
    const frames = vi.spyOn(window, "requestAnimationFrame").mockReturnValue(0);
    await userEvent.click(screen.getByRole("checkbox", { name: /RealTime Monitor/ }));
    expect(await screen.findByText("No hops in this view")).toBeInTheDocument();
    frames.mockRestore();
  });
});
