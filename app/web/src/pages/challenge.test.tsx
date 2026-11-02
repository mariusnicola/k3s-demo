import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { ConnectionStatus } from "../ds";
import { LINE_STOPPED, batch, pending } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Reply } from "../mocks/harness";

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

const ROUTE = "GET /api/batches/farm-a/b-1";

describe("controls say what they do", () => {
  it("REQ-BT-036 a failed write offers Dismiss, not a Try again that retries nothing", async () => {
    const calls = installApi({
      "GET /api/me": meRoute("ada"),
      "GET /api/batches": { body: [batch()] },
      "GET /line": { body: LINE_STOPPED },
      "POST /api/compliance/run": { status: 500 },
    });
    renderApp("/dashboard");
    await userEvent.click(await screen.findByRole("button", { name: "Run nightly now" }));
    const panel = (await screen.findByText("The request failed")).closest("section")!;
    expect(within(panel).queryByRole("button", { name: "Try again" })).toBeNull();
    await userEvent.click(within(panel).getByRole("button", { name: "Dismiss" }));
    expect(screen.queryByText("The request failed")).toBeNull();
    expect(calls.filter((c) => c.url.endsWith("/compliance/run"))).toHaveLength(1);
  });

  it("REQ-BT-036 a 500 on advance offers Dismiss and keeps the batch on screen", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), [ROUTE]: { etag: '"1"', body: batch() }, "POST /api/batches/farm-a/b-1/advance": { status: 500 } });
    renderApp("/batches/farm-a/b-1");
    await userEvent.click(await screen.findByRole("button", { name: "Move to Dose 2" }));
    expect(await screen.findByRole("button", { name: "Dismiss" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Bovine vaccine" })).toBeInTheDocument();
  });

  it("REQ-BT-029 a locked speed control says how to unlock it", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /api/batches": { body: [] }, "GET /line": { body: LINE_STOPPED } });
    renderApp("/dashboard");
    expect(await screen.findByText("Turn RealTime Monitor off to choose a speed.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("checkbox", { name: "RealTime Monitor" }));
    expect(screen.queryByText("Turn RealTime Monitor off to choose a speed.")).toBeNull();
  });

  it("REQ-BT-030 during playback a lost stream does not claim the dots stopped", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /api/batches": { body: [] }, "GET /line": { body: LINE_STOPPED } });
    renderApp("/dashboard");
    await userEvent.click(await screen.findByRole("checkbox", { name: "RealTime Monitor" }));
    act(() => FakeSocket.find("/ws/hops").drop());
    expect(await screen.findByText("Playing the buffer only: the hop stream is lost")).toBeInTheDocument();
    expect(screen.queryByText(/Dots stopped/)).toBeNull();
  });
});

describe("no false refusals", () => {
  it("REQ-BT-006 the button stays busy until the new ETag is on screen, so a second press cannot send a stale one", async () => {
    let release: () => void = () => undefined;
    const answered = new Promise<void>((resolve) => (release = resolve));
    let posted = false;
    const calls = installApi({
      "GET /api/me": meRoute("olivia"),
      [ROUTE]: { etag: '"1"', body: batch() },
      "POST /api/batches/farm-a/b-1/advance": async () => {
        posted = true;
        await answered;
        return { etag: '"2"', body: batch({ version: 2, state: "Dose2" }) };
      },
    });
    renderApp("/batches/farm-a/b-1");
    const button = await screen.findByRole("button", { name: "Move to Dose 2" });
    await userEvent.click(button);
    await waitFor(() => expect(posted).toBe(true));
    await userEvent.click(button);
    expect(calls.filter((c) => c.method === "POST")).toHaveLength(1);
    release();
    expect(await screen.findByText("Moved to Dose 2.")).toBeInTheDocument();
  });

  it("REQ-BT-006 a refused inspection keeps the typed reason", async () => {
    let version = 1;
    installApi({
      "GET /api/me": meRoute("ian"),
      [ROUTE]: () => ({ etag: `"${version}"`, body: batch({ state: "Dose2" }) }),
      "POST /api/batches/farm-a/b-1/inspect": (): Reply => {
        version = 2;
        return { status: 412 };
      },
    });
    renderApp("/batches/farm-a/b-1");
    await userEvent.click(await screen.findByRole("button", { name: "Record inspection" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("radio", { name: /Fail/ }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("textbox", { name: /Reason/ }));
    await userEvent.paste("Seal broken");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Reject batch" }));
    await userEvent.click(await screen.findByRole("button", { name: "Reload batch" }));
    await userEvent.click(await screen.findByRole("button", { name: "Record inspection" }));
    expect(within(screen.getByRole("dialog")).getByRole("textbox", { name: /Reason/ })).toHaveValue("Seal broken");
  });

  it("REQ-BT-036 a hand-edited filter does not reach the API", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch()] } });
    renderApp("/batches?state=Bogus&attention=nonsense");
    await screen.findByRole("link", { name: "Bovine vaccine" });
    expect(calls.find((c) => c.url.includes("/batches"))?.url).not.toContain("Bogus");
  });
});

describe("live updates stay quiet", () => {
  it("REQ-BT-035 the ticking age sits outside the live region", () => {
    const now = Date.now();
    const { container } = render(<ConnectionStatus label="Monitor" state={{ status: "lost", lastDataAt: now - 5000 }} now={now} />);
    const regions = [...container.querySelectorAll('[role="alert"], [role="status"]')];
    expect(regions).toHaveLength(1);
    expect(regions[0]).toHaveTextContent("Monitor: connection lost");
    expect(regions[0]).not.toHaveTextContent(/ago/);
  });

  it("REQ-BT-035 the delayed banner text does not change every second", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), [ROUTE]: { etag: '"1"', body: batch({ pending: pending("sent", "2020-01-01T00:00:00Z") }) } });
    renderApp("/batches/farm-a/b-1");
    const banner = (await screen.findByText("Compliance record is delayed")).closest('[role="status"]')!;
    expect(banner).toHaveTextContent(/Not confirmed since/);
    expect(banner).not.toHaveTextContent(/\d+ (s|min)\b/);
  });
});

describe("dialog focus", () => {
  it("REQ-BT-035 a form dialog starts on its first field", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [] } });
    renderApp("/batches");
    await userEvent.click(await screen.findByRole("button", { name: "New batch" }));
    await waitFor(() => expect(screen.getByRole("textbox", { name: /Product/ })).toHaveFocus());
  });

  it("REQ-BT-035 a confirm dialog starts on Cancel", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), [ROUTE]: { etag: '"1"', body: batch({ state: "Shipped" }) } });
    renderApp("/batches/farm-a/b-1");
    await userEvent.click(await screen.findByRole("button", { name: "Move to Closed" }));
    await waitFor(() => expect(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" })).toHaveFocus());
  });

  it("REQ-BT-035 Escape closes and focus returns to the trigger", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), [ROUTE]: { etag: '"1"', body: batch({ state: "Shipped" }) } });
    renderApp("/batches/farm-a/b-1");
    const trigger = await screen.findByRole("button", { name: "Move to Closed" });
    await userEvent.click(trigger);
    await userEvent.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    await waitFor(() => expect(trigger).toHaveFocus());
  });
});

describe("orientation", () => {
  it("REQ-BT-035 the document title follows the page", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), [ROUTE]: { etag: '"1"', body: batch() } });
    renderApp("/batches/farm-a/b-1");
    await screen.findByRole("heading", { name: "Bovine vaccine" });
    expect(document.title).toBe("Bovine vaccine - Batch Tracker");
  });

  it("REQ-BT-036 newest batches come first", async () => {
    installApi({
      "GET /api/me": meRoute("olivia"),
      "GET /api/batches": { body: [batch({ id: "old", product: "Old", createdAt: "2026-01-01T00:00:00Z" }), batch({ id: "new", product: "New", createdAt: "2026-10-01T00:00:00Z" })] },
    });
    renderApp("/batches");
    const table = await screen.findByRole("table", { name: "Batches" });
    expect(within(table).getAllByRole("link").map((l) => l.textContent)).toEqual(["New", "Old"]);
  });

  it("REQ-BT-012 hashes can be read in full without a hover", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), [ROUTE]: { etag: '"1"', body: batch({ steps: [{ ...batch().steps[0]!, hash: "a".repeat(64) }] }) } });
    renderApp("/batches/farm-a/b-1");
    await userEvent.click(await screen.findByRole("tab", { name: "Compliance records" }));
    expect((await screen.findByRole("table", { name: "Compliance records" })).textContent).toContain("a".repeat(64));
  });
});
