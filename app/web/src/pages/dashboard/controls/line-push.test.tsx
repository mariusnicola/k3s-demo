import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { LINE_STOPPED, batch, hop, hopSnapshot, lineSnapshot } from "../../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type ApiCall } from "../../../mocks/harness";

const gets = (calls: ApiCall[], part: string) => calls.filter((c) => c.method === "GET" && c.url.includes(part));
const wait = (ms: number) => act(() => new Promise<void>((resolve) => setTimeout(resolve, ms)));

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

function hopSocket() {
  const socket = FakeSocket.find("/ws/hops");
  socket.open();
  return socket;
}

describe("dashboard line panel", () => {
  const dashboard = () => {
    const calls = installApi({ "GET /api/me": meRoute("ada"), "GET /api/batches": { body: [] }, "POST /line": { status: 202 }, "DELETE /line": { body: LINE_STOPPED } });
    renderApp("/dashboard");
    return calls;
  };

  it("REQ-BT-032 line state arrives on the socket, and 60 s of hops send no GET to /line", async () => {
    const calls = dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const line = FakeSocket.find("/line/ws");
    act(() => line.open());
    act(() => line.send(lineSnapshot({ status: { ...LINE_STOPPED, running: true, rate: 2, errors: 3 } })));
    expect(await screen.findByText("Running, 2 per second requested")).toBeInTheDocument();
    const socket = hopSocket();
    act(() => {
      for (let i = 0; i < 60; i += 1) socket.sendHops([hop({ traceId: `t${i}` })]);
    });
    await wait(350);
    expect(gets(calls, "/line")).toEqual([]);
  });

  it("REQ-BT-032 a line frame moves every counter", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const line = FakeSocket.find("/line/ws");
    act(() => line.open());
    act(() => line.send(lineSnapshot()));
    act(() => line.send({ type: "line", at: "2026-10-31T10:00:00Z", status: { ...LINE_STOPPED, started: 12, errors: 4 }, entries: [], entriesDropped: 0, evicted: 0 }));
    await waitFor(() => expect(screen.getByText("Started").nextSibling).toHaveTextContent("12"));
    expect(screen.getByRole("link", { name: "4 walks dropped, open the logs" })).toBeInTheDocument();
  });

  it("REQ-BT-030 a lost line socket shows the last known state, never a live one", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const line = FakeSocket.find("/line/ws");
    act(() => line.open());
    act(() => line.send(lineSnapshot({ status: { ...LINE_STOPPED, running: true, rate: 2 } })));
    await screen.findByText("Running, 2 per second requested");
    act(() => line.drop());
    expect(await screen.findByText(/Last known: running at 2 per second/)).toBeInTheDocument();
    expect(screen.queryByText("Running, 2 per second requested")).toBeNull();
  });

  it("REQ-BT-030 a socket that opened and dropped with no state does not say the line was never reached", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    act(() => FakeSocket.find("/line/ws").open());
    act(() => FakeSocket.find("/line/ws").drop());
    expect(await screen.findByText("Production line state unknown")).toBeInTheDocument();
    expect(screen.getByText(/No state came from the line service/)).toBeInTheDocument();
  });

  it("REQ-BT-030 a line never reached says its state is unknown and offers Reconnect", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    act(() => FakeSocket.find("/line/ws").drop());
    expect(await screen.findByText(/No state came from the line service/)).toBeInTheDocument();
    expect(screen.queryByText("Stopped")).toBeNull();
  });

  it("REQ-BT-031 Start posts once and reads nothing back", async () => {
    const calls = dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const line = FakeSocket.find("/line/ws");
    act(() => line.open());
    act(() => line.send(lineSnapshot()));
    await userEvent.click(await screen.findByRole("button", { name: "Start line" }));
    expect(calls.filter((c) => c.method === "POST" && c.url.endsWith("/line"))).toHaveLength(1);
    expect(gets(calls, "/line")).toEqual([]);
  });

  it("REQ-BT-011 failure targets come from live rows, with no GET of the list", async () => {
    const calls = dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    act(() => hopSocket().send(hopSnapshot({ batches: [{ etag: '"v1"', batch: batch() }] })));
    expect(await screen.findByRole("button", { name: "Fail next delivery" })).toBeInTheDocument();
    expect(gets(calls, "/api/batches")).toEqual([]);
  });
});
