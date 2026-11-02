import userEvent from "@testing-library/user-event";
import { screen } from "@testing-library/react";
import axe from "axe-core";
import { vi } from "vitest";
import { LINE_STOPPED, batch, hopSnapshot, lineSnapshot, pending } from "../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, renderInRouter, useFakeSockets } from "../../mocks/harness";
import { CatalogView, buildCatalog } from "../design-system/catalog";

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

const ALERT = { batchId: "b-1", farmId: "farm-a", product: "Bovine vaccine", type: "Dose2Overdue", at: "2026-10-30T10:00:00Z", detail: null };

describe("alerts", () => {
  it("REQ-BT-013 lists alerts with a link to the batch", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    FakeSocket.greet("/ws/hops", hopSnapshot({ alerts: [ALERT] }));
    renderApp("/alerts");
    expect(await screen.findByText("Dose 2 overdue")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Bovine vaccine" })).toHaveAttribute("href", "/batches/farm-a/b-1");
  });

  it("REQ-BT-011 a blocked alert shows the error, and a missing time reads as a word", async () => {
    installApi({ "GET /api/me": meRoute("ada") });
    FakeSocket.greet("/ws/hops", hopSnapshot({ alerts: [{ ...ALERT, type: "Blocked", at: null, detail: "poison" }] }));
    renderApp("/alerts");
    await userEvent.click(await screen.findByRole("button", { name: /details of/ }));
    expect(await screen.findByText("poison")).toBeInTheDocument();
    expect(screen.getByText("Not recorded")).toBeInTheDocument();
  });

  it("REQ-BT-036 empty says what will fill it, with no button to press", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    renderApp("/alerts");
    expect(await screen.findByText(/Overdue doses appear here after the nightly run. Blocked batches appear as they happen./)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Refresh" })).toBeNull();
  });

});

/** Colour contrast is covered by the token test; jsdom cannot compute layout for it. */
async function axeViolations() {
  const result = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
  return result.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
}

/** One state at a time: axe's cost grows faster than the document, so an entry with a map per state outruns the same states run apart. */
const CATALOG_STATES = buildCatalog(Date.now()).flatMap((item) => item.states.map((state): [string, typeof item] => [`${item.name}, ${state.label}`, { ...item, states: [state] }]));

describe("accessibility of real pages, in jsdom", () => {
  it("REQ-BT-035 sign in has no axe violations", async () => {
    installApi({});
    renderApp("/signin");
    await screen.findByRole("heading", { name: /Sign in/ });
    expect(await axeViolations()).toEqual([]);
  });

  it("REQ-BT-035 batches has no axe violations", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch(), batch({ id: "b-2", pending: pending("dead") })] } });
    renderApp("/batches");
    await screen.findAllByRole("link", { name: "Bovine vaccine" });
    expect(await axeViolations()).toEqual([]);
  });

  it("REQ-BT-035 batch has no axe violations, blocked with disabled controls", async () => {
    installApi({ "GET /api/me": meRoute("viewer"), "GET /api/batches/farm-a/b-1": { etag: '"1"', body: batch({ pending: pending("dead") }) } });
    renderApp("/batches/farm-a/b-1");
    await screen.findByRole("heading", { name: "Bovine vaccine" });
    expect(await axeViolations()).toEqual([]);
  });

  it("REQ-BT-035 alerts has no axe violations", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    FakeSocket.greet("/ws/hops", hopSnapshot({ alerts: [ALERT] }));
    renderApp("/alerts");
    await screen.findByText("Dose 2 overdue");
    expect(await axeViolations()).toEqual([]);
  });

  it("REQ-BT-035 dashboard has no axe violations", async () => {
    installApi({ "GET /api/me": meRoute("ada") });
    FakeSocket.greet("/ws/hops", hopSnapshot({ batches: [{ etag: '"v1"', batch: batch() }] }));
    FakeSocket.greet("/line/ws", lineSnapshot({ status: LINE_STOPPED }));
    renderApp("/dashboard");
    await screen.findByRole("button", { name: "Start line" });
    expect(await axeViolations()).toEqual([]);
  });

  it.each(CATALOG_STATES)("REQ-BT-035 catalog entry %s has no axe violations", async (_name, item) => {
    renderInRouter(<CatalogView items={[item]} />);
    expect(await axeViolations()).toEqual([]);
  });
});
