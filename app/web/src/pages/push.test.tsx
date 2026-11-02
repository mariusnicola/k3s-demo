import { act, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { USERS, alert, hop, hopSnapshot, lineLogs, lineSnapshot, logEntry } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type ApiCall } from "../mocks/harness";

const gets = (calls: ApiCall[], part: string) => calls.filter((c) => c.method === "GET" && c.url.includes(part));
const wait = (ms: number) => act(() => new Promise<void>((resolve) => setTimeout(resolve, ms)));

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

function hopSocket() {
  const socket = FakeSocket.find("/ws/hops");
  socket.open();
  return socket;
}

describe("no socket carries a token", () => {
  it("REQ-BT-032 the hop socket url has no token", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [] } });
    renderApp("/batches");
    await screen.findByRole("heading", { name: "Batches" });
    expect(FakeSocket.find("/ws/hops").url).not.toContain("token");
  });
});

describe("frames that land around a read or a page change", () => {
  it("REQ-BT-045 an entry pushed during the first read is kept when the read answers", async () => {
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => (release = resolve));
    installApi({
      "GET /api/me": meRoute("ada"),
      "GET /line/logs": async () => {
        await gate;
        return { body: lineLogs() };
      },
    });
    renderApp("/logs?level=error");
    await screen.findByRole("heading", { name: "Logs" });
    const line = FakeSocket.find("/line/ws");
    act(() => line.open());
    act(() => line.send(lineSnapshot({ entries: [logEntry({ at: "2026-10-31T11:00:00.000Z", message: "Pushed during the read." })] })));
    await act(async () => release());
    expect(await within(await screen.findByRole("table", { name: "Log entries" })).findByText("Pushed during the read.")).toBeInTheDocument();
  });

  it("REQ-BT-028 hops seen before the dashboard opened are on it, and the socket stays the same one", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /api/batches": { body: [] } });
    renderApp("/batches");
    await screen.findByRole("heading", { name: "Batches" });
    const socket = hopSocket();
    act(() => socket.send(hopSnapshot({ hops: [hop({ traceId: "seen-before" })] })));
    await userEvent.click(screen.getByRole("link", { name: "Dashboard" }));
    // The rows arrive on the page's next snapshot, so the page and its rows each get their own wait.
    await screen.findByRole("heading", { name: "Recent hops" });
    expect(await screen.findByRole("button", { name: "Follow trace seen-before" })).toBeInTheDocument();
    expect(FakeSocket.all.filter((s) => s.url.endsWith("/ws/hops"))).toHaveLength(1);
  });

  it("REQ-BT-016 signing out drops the rows, so the next user never sees them", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "POST /api/auth/dev-login": { body: USERS.olivia } });
    renderApp("/alerts");
    await screen.findByRole("heading", { name: "Alerts" });
    act(() => hopSocket().send(hopSnapshot({ alerts: [alert({ product: "Secret lot" })] })));
    expect(await screen.findByRole("link", { name: "Secret lot" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /Switch user/ }));
    await userEvent.click(await screen.findByRole("button", { name: /Continue as olivia/ }));
    expect(await screen.findByRole("status", { name: "Loading alerts" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Secret lot" })).toBeNull();
  });
});

describe("session", () => {
  it("REQ-BT-032 a 4401 close goes to sign in, with no /me read", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia") });
    renderApp("/alerts");
    await screen.findByRole("heading", { name: "Alerts" });
    const socket = hopSocket();
    const before = gets(calls, "/me").length;
    act(() => socket.drop(4401));
    expect(await screen.findByText("Your session ended")).toBeInTheDocument();
    expect(gets(calls, "/me")).toHaveLength(before);
  });

  it("REQ-BT-032 a plain drop reads nothing", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia") });
    renderApp("/alerts");
    await screen.findByRole("heading", { name: "Alerts" });
    const socket = hopSocket();
    const before = calls.length;
    act(() => socket.drop());
    await wait(100);
    expect(calls.slice(before)).toEqual([]);
  });
});
