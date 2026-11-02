import { act, screen, waitFor, within } from "@testing-library/react";
import { vi } from "vitest";
import { SUMMARY, batch, hop, hopSnapshot } from "../../mocks/fixtures";
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

describe("batches page", () => {
  it("REQ-BT-032 totals come from the snapshot, with no GET of the summary", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch()] } });
    renderApp("/batches");
    await screen.findByRole("link", { name: "Bovine vaccine" });
    act(() => hopSocket().send(hopSnapshot({ summary: { ...SUMMARY, blocked: 5 } })));
    expect(await screen.findByRole("link", { name: /Blocked/ })).toHaveTextContent("5");
    expect(gets(calls, "/summary")).toEqual([]);
  });

  it("REQ-BT-032 a summary frame moves the totals", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch()] } });
    renderApp("/batches");
    await screen.findByRole("link", { name: "Bovine vaccine" });
    const socket = hopSocket();
    act(() => socket.send(hopSnapshot()));
    act(() => socket.send({ type: "summary", summary: { ...SUMMARY, blocked: 9 } }));
    await waitFor(() => expect(screen.getByRole("link", { name: /Blocked/ })).toHaveTextContent("9"));
  });

  it("REQ-BT-032 a batch frame updates its row and 500 hops cost no GET at all", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch({ state: "Dose1" })] } });
    renderApp("/batches");
    await screen.findByRole("link", { name: "Bovine vaccine" });
    const before = calls.length;
    const socket = hopSocket();
    act(() => socket.send(hopSnapshot()));
    act(() => socket.sendHops(Array.from({ length: 500 }, (_, i) => hop({ traceId: `t${i}` }))));
    act(() => socket.send({ type: "batch", etag: '"v2"', batch: batch({ version: 2, state: "Shipped" }) }));
    expect(await within(await screen.findByRole("table", { name: "Batches" })).findByText("Shipped")).toBeInTheDocument();
    await wait(350);
    expect(calls.slice(before)).toEqual([]);
  });

  it("REQ-BT-032 a new batch that matches the filter appears; one that does not stays out", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [] } });
    renderApp("/batches?state=Dose1");
    await screen.findByText(/No batches match/);
    const socket = hopSocket();
    act(() => socket.send(hopSnapshot()));
    act(() => socket.send({ type: "batch", etag: '"v1"', batch: batch({ id: "b-yes", product: "Matches", state: "Dose1" }) }));
    act(() => socket.send({ type: "batch", etag: '"v1"', batch: batch({ id: "b-no", product: "Other", state: "Dose2" }) }));
    expect(await screen.findByRole("link", { name: "Matches" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Other" })).toBeNull();
  });

  it("REQ-BT-032 reopening the socket sends no GET; the snapshot brings the state", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch()] } });
    renderApp("/batches");
    await screen.findByRole("link", { name: "Bovine vaccine" });
    const socket = hopSocket();
    act(() => socket.send(hopSnapshot()));
    const before = calls.length;
    act(() => socket.drop());
    await waitFor(() => expect(socket).not.toBe(FakeSocket.find("/ws/hops")));
    const again = FakeSocket.find("/ws/hops");
    act(() => again.open());
    act(() => again.send(hopSnapshot({ batches: [{ etag: '"v3"', batch: batch({ version: 3, state: "Inspected" }) }] })));
    expect(await within(await screen.findByRole("table", { name: "Batches" })).findByText("Inspected")).toBeInTheDocument();
    expect(calls.slice(before)).toEqual([]);
  });

  it("REQ-BT-032 a row pushed out of the kept live rows does not go back to its first read", async () => {
    const flags = [{ type: "overdue", at: "2026-01-01T00:00:00Z" }];
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch({ state: "Dose1", flags })] } });
    renderApp("/batches?attention=flagged");
    await screen.findByRole("link", { name: "Bovine vaccine" });
    const socket = hopSocket();
    act(() => socket.send(hopSnapshot()));
    act(() => socket.send({ type: "batch", etag: '"v2"', batch: batch({ version: 2, state: "Shipped", flags }) }));
    const table = await screen.findByRole("table", { name: "Batches" });
    await within(table).findByText("Shipped");
    await wait(300);
    act(() => {
      for (let i = 0; i < 1100; i += 1) socket.send({ type: "batch", etag: '"v1"', batch: batch({ id: `other-${i}`, product: "Other", createdAt: "2020-01-01T00:00:00Z" }) });
    });
    await wait(300);
    expect(within(table).getByText("Shipped")).toBeInTheDocument();
  });

  it("REQ-BT-030 with the stream lost before any snapshot, totals say they are unknown", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch()] } });
    renderApp("/batches");
    await screen.findByRole("link", { name: "Bovine vaccine" });
    act(() => FakeSocket.find("/ws/hops").drop());
    expect(await screen.findByText(/Totals are unknown until the live stream returns/)).toBeInTheDocument();
  });

  it("REQ-BT-032 there is no Refresh to fall back on", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch()] } });
    renderApp("/batches");
    await screen.findByRole("link", { name: "Bovine vaccine" });
    expect(screen.queryByRole("button", { name: "Refresh" })).toBeNull();
  });
});
