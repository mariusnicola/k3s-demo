import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Batch, BatchState } from "../../../api/types";
import { batch, hopSnapshot, pending, step } from "../../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../../../mocks/harness";

const NOW = new Date(2026, 9, 31, 12, 0, 0).getTime();
const TODAY = new Date(NOW - 3_600_000).toISOString();
const made = (id: string, state: BatchState, over: Partial<Batch> = {}) => batch({ id, state, product: `Product ${id}`, createdAt: TODAY, steps: [step(state, true, { at: TODAY })], ...over });
const stat = (label: string) => [...document.querySelectorAll(".ds-stat")].find((s) => s.textContent?.includes(label))!;
const EMPTY = { byState: {}, pending: 0, blocked: 0, flagged: 0 };

function byState(rows: Batch[]) {
  return (url: string) => {
    const state = new URL(url, "http://x").searchParams.get("state");
    return { body: rows.filter((b) => !state || b.state === state) };
  };
}


beforeEach(() => {
  useFakeSockets();
  localStorage.clear();
  vi.spyOn(Date, "now").mockReturnValue(NOW);
});

afterEach(() => void vi.restoreAllMocks());

describe("REQ-BT-055 role dashboards under failure", () => {
  it("REQ-BT-055 a failed count says so and offers a retry", async () => {
    let fail = true;
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": (url: string) => (fail && url.includes("state=Closed") ? { status: 500 } : { body: [made("c", "Closed")] }) });
    FakeSocket.greet("/ws/hops", hopSnapshot({ summary: EMPTY }));
    renderApp("/dashboard");
    expect(await screen.findByText("The request failed")).toBeInTheDocument();
    expect(stat("Closed today")).toHaveTextContent("Unknown");
    fail = false;
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    await waitFor(() => expect(stat("Closed today")).toHaveTextContent("1"));
  });

  it("REQ-BT-055 a count still loading says loading, not unknown", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": () => new Promise(() => undefined) });
    FakeSocket.greet("/ws/hops", hopSnapshot({ summary: EMPTY }));
    renderApp("/dashboard");
    await screen.findByText("Closed today");
    expect(stat("Closed today")).toHaveTextContent("Loading");
  });

  it("REQ-BT-055 a full list with nothing waiting does not say nothing is waiting", async () => {
    const closed = Array.from({ length: 200 }, (_, i) => made(`c${i}`, "Closed"));
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: closed } });
    FakeSocket.greet("/ws/hops", hopSnapshot({ summary: EMPTY }));
    renderApp("/dashboard");
    expect(await screen.findByText("Nothing waiting in the newest 200 batches")).toBeInTheDocument();
    expect(screen.queryByText("Nothing is waiting for you")).toBeNull();
  });

  it("REQ-BT-055 a full list says it is only part of the picture, however few rows show", async () => {
    const rows = [...Array.from({ length: 199 }, (_, i) => made(`c${i}`, "Closed")), made("a", "Dose1")];
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: rows } });
    FakeSocket.greet("/ws/hops", hopSnapshot({ summary: EMPTY }));
    renderApp("/dashboard");
    expect(await screen.findByText(/Only the newest 200 batches were read/)).toBeInTheDocument();
  });

  it("REQ-BT-055 a list that grows to the cap by pushes becomes a floor", async () => {
    const rows = Array.from({ length: 199 }, (_, i) => made(`c${i}`, "Rejected"));
    installApi({ "GET /api/me": meRoute("ian"), "GET /api/batches": byState(rows) });
    FakeSocket.greet("/ws/hops", hopSnapshot({ summary: EMPTY }));
    renderApp("/dashboard");
    await waitFor(() => expect(stat("Failed today")).toHaveTextContent("199"));
    act(() => FakeSocket.find("/ws/hops").send({ type: "batch", etag: '"v2"', batch: made("new", "Rejected", { version: 2 }) }));
    await waitFor(() => expect(stat("Failed today")).toHaveTextContent("At least 200"));
  });

  it("REQ-BT-055 an operator sees what is held and flagged, and no all clear beside it", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [] } });
    FakeSocket.greet("/ws/hops", hopSnapshot({ summary: { ...EMPTY, pending: 3, flagged: 2 } }));
    renderApp("/dashboard");
    await screen.findByText("Nothing is waiting for you");
    expect(stat("Event in flight")).toHaveTextContent("3");
    expect(stat("Event in flight")).toHaveAttribute("href", "/batches?attention=inflight");
    expect(stat("Flagged")).toHaveAttribute("href", "/alerts");
    expect(screen.getByText(/3 batches have an event in flight, and may join this list when it lands/)).toBeInTheDocument();
  });

  it("REQ-BT-055 after the feed comes back the list offers one read and makes none itself", async () => {
    let rows = [made("a", "Dose1")];
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": () => ({ body: rows }) });
    FakeSocket.greet("/ws/hops", hopSnapshot({ summary: EMPTY }));
    renderApp("/dashboard");
    await screen.findByText("Product a");
    rows = [made("b", "Dose1")];
    await act(async () => FakeSocket.find("/ws/hops").drop());
    await screen.findByText(/This list may be out of date/);
    await waitFor(() => expect(screen.queryByText(/This list may be out of date/)).toBeNull(), { timeout: 8000 });
    await userEvent.click(await screen.findByRole("button", { name: "Read again" }));
    expect(await screen.findByText("Product b")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Read again" })).toBeNull();
  });

  it("REQ-BT-055 a row under the pointer stays while live data moves", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [made("a", "Dose1")] } });
    FakeSocket.greet("/ws/hops", hopSnapshot({ summary: EMPTY }));
    renderApp("/dashboard");
    const table = await screen.findByRole("table", { name: "Waiting for your next step" });
    await userEvent.hover(within(table).getByText("Product a"));
    act(() => FakeSocket.find("/ws/hops").send({ type: "batch", etag: '"v2"', batch: made("a", "Dose2", { version: 2 }) }));
    await act(async () => undefined);
    expect(screen.getByText("Product a")).toBeInTheDocument();
    expect(screen.getByText("List held while the pointer or focus is inside it.")).toBeInTheDocument();
  });

  it("REQ-BT-055 a read that fails after the list is on screen says so and keeps the rows", async () => {
    let fail = false;
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": () => (fail ? { status: 500 } : { body: [made("a", "Dose1")] }) });
    FakeSocket.greet("/ws/hops", hopSnapshot({ summary: EMPTY }));
    renderApp("/dashboard");
    await screen.findByText("Product a");
    fail = true;
    await act(async () => FakeSocket.find("/ws/hops").drop());
    await userEvent.click(await screen.findByRole("button", { name: "Read again" }, { timeout: 8000 }));
    expect((await screen.findAllByText("The request failed")).length).toBeGreaterThan(0);
    expect(screen.getByText("Product a")).toBeInTheDocument();
  });

  it("REQ-BT-055 a lost feed marks the list too", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [made("a", "Dose1")] } });
    FakeSocket.greet("/ws/hops", hopSnapshot({ summary: EMPTY }));
    renderApp("/dashboard");
    await screen.findByRole("table", { name: "Waiting for your next step" });
    await act(async () => FakeSocket.find("/ws/hops").drop());
    expect(await screen.findByText(/This list may be out of date/)).toBeInTheDocument();
  });

  it("REQ-BT-055 a batch held by an event is shown as held, with no way to inspect it", async () => {
    installApi({ "GET /api/me": meRoute("ian"), "GET /api/batches": byState([made("go", "Dose2"), made("held", "Dose2", { pending: pending("dead") })]) });
    FakeSocket.greet("/ws/hops", hopSnapshot({ summary: { ...EMPTY, byState: { Dose2: 2 } } }));
    renderApp("/dashboard");
    const table = await screen.findByRole("table", { name: "Waiting for inspection" });
    expect(within(table).getByRole("link", { name: "Inspect Product go" })).toBeInTheDocument();
    expect(within(table).getByRole("link", { name: "Open Product held" })).toBeInTheDocument();
    expect(within(table).getByText("Blocked")).toBeInTheDocument();
  });

  it("REQ-BT-055 the floor stays a floor when one row leaves a full list", async () => {
    const rows = Array.from({ length: 200 }, (_, i) => made(`c${i}`, "Rejected"));
    installApi({ "GET /api/me": meRoute("ian"), "GET /api/batches": byState(rows) });
    FakeSocket.greet("/ws/hops", hopSnapshot({ summary: EMPTY }));
    renderApp("/dashboard");
    await waitFor(() => expect(stat("Failed today")).toHaveTextContent("At least 200"));
    act(() => FakeSocket.find("/ws/hops").send({ type: "batch", etag: '"v2"', batch: made("c1", "Closed", { version: 2 }) }));
    await waitFor(() => expect(stat("Failed today")).toHaveTextContent("At least 199"));
  });
});
