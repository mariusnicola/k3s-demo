import { act, screen, waitFor, within } from "@testing-library/react";
import { vi } from "vitest";
import { parseHopFrame } from "../logic/feed/live";
import { SUMMARY, alert, batch, hopSnapshot } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../mocks/harness";

const NUMBERS_SHORT = /Event in flight, Blocked and Flagged may read low/;
const ALERTS_SHORT = /this list may be missing alerts/;
const summaryFrame = (capped: boolean) => ({ type: "summary", summary: SUMMARY, reconciledAt: "2026-10-31T10:00:00Z", capped });

function hopSocket() {
  const socket = FakeSocket.find("/ws/hops");
  socket.open();
  return socket;
}

async function batches(snapshot: Record<string, unknown>) {
  installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch()] } });
  renderApp("/batches");
  await screen.findByRole("link", { name: "Bovine vaccine" });
  const socket = hopSocket();
  act(() => socket.send(hopSnapshot(snapshot)));
  await screen.findByRole("link", { name: /Blocked/ });
  return socket;
}

async function alerts(snapshot: Record<string, unknown>) {
  installApi({ "GET /api/me": meRoute("olivia") });
  renderApp("/alerts");
  await screen.findByRole("heading", { name: "Alerts" });
  act(() => hopSocket().send(hopSnapshot(snapshot)));
}

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

describe("B-026 a capped scan", () => {
  it.each([
    ["snapshot", hopSnapshot({ capped: true })],
    ["summary", summaryFrame(true)],
  ])("B-026 a %s frame carries it", (_type, frame) => {
    expect(parseHopFrame(JSON.stringify(frame))).toMatchObject({ capped: true });
  });

  it("B-026 a frame without it is not capped", () => {
    expect(parseHopFrame(JSON.stringify(hopSnapshot({ capped: undefined })))).toMatchObject({ capped: false });
  });

  it("B-026 the batch totals say their numbers may read low", async () => {
    await batches({ capped: true });
    const note = await screen.findByText(NUMBERS_SHORT);
    expect(note.parentElement).toContainElement(screen.getByRole("link", { name: /Blocked/ }));
  });

  it("B-026 an uncapped snapshot says nothing", async () => {
    await batches({ capped: false });
    expect(screen.queryByText(NUMBERS_SHORT)).not.toBeInTheDocument();
  });

  it("B-026 a summary frame turns it on and off", async () => {
    const socket = await batches({});
    act(() => socket.send(summaryFrame(true)));
    expect(await screen.findByText(NUMBERS_SHORT)).toBeInTheDocument();
    act(() => socket.send(summaryFrame(false)));
    await waitFor(() => expect(screen.queryByText(NUMBERS_SHORT)).not.toBeInTheDocument());
  });

  it("B-026 frames that do not carry it leave it standing", async () => {
    const socket = await batches({ capped: true });
    await screen.findByText(NUMBERS_SHORT);
    act(() => socket.send({ type: "alerts", alerts: [] }));
    act(() => socket.send({ type: "batch", etag: '"v2"', batch: batch({ id: "b-2", product: "Second vaccine" }) }));
    await screen.findByRole("link", { name: "Second vaccine" });
    expect(screen.getByText(NUMBERS_SHORT)).toBeInTheDocument();
  });

  it("B-026 the operator's numbers say so too", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch()] } });
    renderApp("/dashboard");
    await screen.findByRole("heading", { name: "Dashboard" });
    act(() => hopSocket().send(hopSnapshot({ capped: true })));
    const note = await screen.findByText(NUMBERS_SHORT);
    expect(note.parentElement).toContainElement(screen.getByRole("link", { name: /Blocked/ }));
  });

  it("B-026 the operator's note needs numbers beside it", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch()] } });
    renderApp("/dashboard");
    await screen.findByRole("heading", { name: "Dashboard" });
    act(() => hopSocket().send(hopSnapshot({ capped: true, reconciledAt: null })));
    await screen.findByText("The live service has not read the store yet");
    expect(screen.queryByText(NUMBERS_SHORT)).not.toBeInTheDocument();
  });

  it("B-026 a list of alerts says it may be missing some", async () => {
    await alerts({ capped: true, alerts: [alert()] });
    const panel = (await screen.findByRole("table", { name: "Open alerts" })).closest("section")!;
    expect(within(panel).getByText(ALERTS_SHORT)).toBeInTheDocument();
  });

  it("B-026 an empty list of alerts does not claim none are open", async () => {
    await alerts({ capped: true, alerts: [] });
    expect(await screen.findByText(ALERTS_SHORT)).toBeInTheDocument();
    expect(screen.queryByText("No open alerts")).not.toBeInTheDocument();
  });

  it("B-026 uncapped alerts say nothing extra", async () => {
    await alerts({ alerts: [] });
    expect(await screen.findByText("No open alerts")).toBeInTheDocument();
    expect(screen.queryByText(ALERTS_SHORT)).not.toBeInTheDocument();
  });
});
