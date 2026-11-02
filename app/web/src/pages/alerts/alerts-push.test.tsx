import { act, screen } from "@testing-library/react";
import { vi } from "vitest";
import { alert, hopSnapshot } from "../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type ApiCall } from "../../mocks/harness";

const gets = (calls: ApiCall[], part: string) => calls.filter((c) => c.method === "GET" && c.url.includes(part));

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

function hopSocket() {
  const socket = FakeSocket.find("/ws/hops");
  socket.open();
  return socket;
}

describe("alerts page", () => {
  it("REQ-BT-032 alerts come from frames, with no GET", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia") });
    renderApp("/alerts");
    await screen.findByRole("heading", { name: "Alerts" });
    const socket = hopSocket();
    act(() => socket.send(hopSnapshot({ alerts: [] })));
    expect(await screen.findByText("No open alerts")).toBeInTheDocument();
    act(() => socket.send({ type: "alerts", alerts: [alert()] }));
    expect(await screen.findByRole("link", { name: "Bovine vaccine" })).toBeInTheDocument();
    expect(gets(calls, "/alerts")).toEqual([]);
  });

  it("REQ-BT-036 before the snapshot the page shows loading", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    renderApp("/alerts");
    expect(await screen.findByRole("status", { name: "Loading alerts" })).toBeInTheDocument();
  });

  it("REQ-BT-030 lost before any snapshot says alerts are unknown, with a way to reconnect", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    renderApp("/alerts");
    await screen.findByRole("heading", { name: "Alerts" });
    act(() => FakeSocket.find("/ws/hops").drop());
    expect(await screen.findByText("Alerts are unknown")).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Reconnect now" }).length).toBeGreaterThan(0);
  });
});
