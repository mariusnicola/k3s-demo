import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { LINE_STOPPED, hop, lineLogs, lineSnapshot, logEntry } from "../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type ApiCall } from "../../mocks/harness";

const gets = (calls: ApiCall[], part: string) => calls.filter((c) => c.method === "GET" && c.url.includes(part));
const wait = (ms: number) => act(() => new Promise<void>((resolve) => setTimeout(resolve, ms)));

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

function hopSocket() {
  const socket = FakeSocket.find("/ws/hops");
  socket.open();
  return socket;
}

describe("logs page", () => {
  const logs = (path = "/logs?level=error&source=line") => {
    const calls = installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": { body: lineLogs() } });
    renderApp(path);
    return calls;
  };
  const open = async (status = LINE_STOPPED) => {
    await screen.findByRole("heading", { name: "Logs" });
    const line = FakeSocket.find("/line/ws");
    act(() => line.open());
    act(() => line.send(lineSnapshot({ status: { ...status, errors: 1, errorsByReason: { "event-timeout": 1 } } })));
    return line;
  };

  it("REQ-BT-032 one GET per filter, then frames; hops never read logs", async () => {
    const calls = logs();
    const line = await open();
    await screen.findByRole("table", { name: "Log entries" });
    act(() => hopSocket().sendHops([hop()]));
    act(() => line.send({ type: "line", at: "2026-10-31T11:00:00Z", status: { ...LINE_STOPPED, errors: 2, errorsByReason: { "event-timeout": 2 } }, entries: [logEntry({ at: "2026-10-31T11:00:00.000Z", message: "A second failure." })], entriesDropped: 0, evicted: 0 }));
    expect(await within(screen.getByRole("table", { name: "Log entries" })).findByText("A second failure.")).toBeInTheDocument();
    await wait(350);
    expect(gets(calls, "/line/logs")).toHaveLength(1);
    expect(calls.filter((c) => c.url.endsWith("/line"))).toEqual([]);
  });

  it("REQ-BT-045 a pushed entry keeps the counter and the log in agreement", async () => {
    logs();
    const line = await open();
    await screen.findByText("Matches the Dashboard: 1 error.");
    act(() => line.send({ type: "line", at: "2026-10-31T11:00:00Z", status: { ...LINE_STOPPED, errors: 2, errorsByReason: { "event-timeout": 2 } }, entries: [logEntry({ at: "2026-10-31T11:00:00.000Z" })], entriesDropped: 0, evicted: 0 }));
    expect(await screen.findByText("Matches the Dashboard: 2 errors.")).toBeInTheDocument();
  });

  it("REQ-BT-045 entries too fast to fit a frame offer one read of the newest", async () => {
    const calls = logs();
    const line = await open();
    await screen.findByRole("table", { name: "Log entries" });
    act(() => line.send({ type: "line", at: "2026-10-31T11:00:00Z", status: LINE_STOPPED, entries: [], entriesDropped: 12, evicted: 0 }));
    await userEvent.click(await screen.findByRole("button", { name: "Read the newest entries again" }));
    await waitFor(() => expect(gets(calls, "/line/logs")).toHaveLength(2));
  });

  it("REQ-BT-045 a reopened snapshot past the list offers the one read that fills the gap", async () => {
    const calls = logs();
    const line = await open();
    await screen.findByRole("table", { name: "Log entries" });
    act(() => line.drop());
    await waitFor(() => expect(line).not.toBe(FakeSocket.find("/line/ws")));
    const again = FakeSocket.find("/line/ws");
    act(() => again.open());
    act(() => again.send(lineSnapshot({ entries: [logEntry({ at: "2026-10-31T12:00:00.000Z" })], entriesDropped: 300 })));
    await userEvent.click(await screen.findByRole("button", { name: "Load missed entries" }));
    await waitFor(() => expect(gets(calls, "/line/logs")).toHaveLength(2));
  });

  it("REQ-BT-030 a lost line socket says entries may be missing", async () => {
    logs();
    const line = await open();
    await screen.findByRole("table", { name: "Log entries" });
    act(() => line.drop());
    expect(await screen.findByText("Live updates are lost")).toBeInTheDocument();
  });

  it("REQ-BT-032 there is no Refresh and no quiet page warning", async () => {
    logs();
    await open();
    await screen.findByRole("table", { name: "Log entries" });
    expect(screen.queryByRole("button", { name: "Refresh" })).toBeNull();
    expect(screen.queryByText(/none have for a while/)).toBeNull();
  });
});
