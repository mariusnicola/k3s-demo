import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import type { Batch } from "../../api/types";
import { BATCH_LIST_LIMIT } from "../../config";
import { batch, hopSnapshot, pending, step } from "../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../../mocks/harness";

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

describe("batches page tells the truth about its totals", () => {
  const routes = (rows: Batch[]) => ({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: rows } });

  it("REQ-BT-036 Blocked and Flagged open Alerts, which has no row limit", async () => {
    installApi(routes([batch()]));
    FakeSocket.greet("/ws/hops", hopSnapshot());
    renderApp("/batches");
    expect(await screen.findByRole("link", { name: /Blocked/ })).toHaveAttribute("href", "/alerts");
    expect(screen.getByRole("link", { name: /Flagged/ })).toHaveAttribute("href", "/alerts");
  });

  it("REQ-BT-036 at the list limit an empty filter says it only saw the newest rows", async () => {
    const rows = Array.from({ length: BATCH_LIST_LIMIT }, (_, i) => batch({ id: `b-${i}` }));
    installApi(routes(rows));
    renderApp("/batches?attention=blocked");
    expect(await screen.findByText("No batches match these filters")).toBeInTheDocument();
    const note = screen.getByText(/Showing the newest 200 batches/);
    expect(within(note).getByRole("link", { name: "Alerts" })).toHaveAttribute("href", "/alerts");
  });
});

describe("rows stay under the operator", () => {
  it("REQ-BT-035 new batches do not move rows while the pointer is in the table", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch({ id: "b-old", product: "Old product" })] } });
    renderApp("/batches");
    const link = await screen.findByRole("link", { name: "Old product" });
    await userEvent.hover(link);
    expect(await screen.findByText(/List held while/)).toBeInTheDocument();
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.send({ type: "batch", etag: '"v1"', batch: batch({ id: "b-new", product: "New product", createdAt: "2030-01-01T00:00:00Z" }) }));
    await act(() => new Promise<void>((resolve) => setTimeout(resolve, 200)));
    expect(screen.queryByRole("link", { name: "New product" })).toBeNull();
    await userEvent.unhover(link);
    expect(await screen.findByRole("link", { name: "New product" })).toBeInTheDocument();
  });
});

describe("a hold ends with its table", () => {
  it("REQ-BT-035 a held row that leaves the filter does not come back after Clear filters", async () => {
    const old = batch({ id: "b-old", product: "Old product", pending: pending("sent", new Date().toISOString()), steps: [step("Dose1", false, { at: new Date().toISOString() })] });
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [old] } });
    renderApp("/batches?state=Dose1&attention=inflight");
    await userEvent.hover(await screen.findByRole("link", { name: "Old product" }));
    expect(await screen.findByText(/List held while/)).toBeInTheDocument();
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.send({ type: "batch", etag: '"v2"', batch: { ...old, version: 2, state: "Dose2" } }));
    act(() => socket.send({ type: "batch", etag: '"v1"', batch: batch({ id: "b-new", product: "New product" }) }));
    await act(() => new Promise<void>((resolve) => setTimeout(resolve, 200)));
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "Attention" }), "All batches");
    expect(await screen.findByRole("link", { name: "New product" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Old product" })).toBeNull();
  });

  it("REQ-BT-035 a browser side filter applies with the pointer parked on the table", async () => {
    const rows = [batch({ id: "b-a", product: "Product A" }), batch({ id: "b-b", product: "Product B", pending: pending("dead", null, "refused") })];
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: rows } });
    renderApp("/batches");
    await userEvent.hover(await screen.findByRole("link", { name: "Product A" }));
    await screen.findByText(/List held while/);
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "Attention" }), "Blocked");
    await waitFor(() => expect(screen.queryByRole("link", { name: "Product A" })).toBeNull());
    expect(screen.getByRole("link", { name: "Product B" })).toBeInTheDocument();
  });

  it("REQ-BT-035 a held table stays when live rows reach zero, and says when it was held", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch({ id: "b-old", product: "Old product" })] } });
    renderApp("/batches?state=Dose1");
    await userEvent.hover(await screen.findByRole("link", { name: "Old product" }));
    await screen.findByText(/List held while/);
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.send({ type: "batch", etag: '"v2"', batch: batch({ id: "b-old", product: "Old product", version: 2, state: "Dose2" }) }));
    await act(() => new Promise<void>((resolve) => setTimeout(resolve, 200)));
    const link = screen.getByRole("link", { name: "Old product" });
    expect(screen.getByText(/Held at/)).toBeInTheDocument();
    await userEvent.unhover(link);
    expect(await screen.findByText("No batches match these filters")).toBeInTheDocument();
  });
});
