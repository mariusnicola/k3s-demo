import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { MONITOR_WS } from "../../../config";
import type { Batch } from "../../../api/types";
import { LINE_STOPPED, REPORT, batch, hopSnapshot, lineSnapshot } from "../../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Handler, type Reply } from "../../../mocks/harness";

function dashboard(extra: Record<string, Reply | Handler> = {}, rows: Batch[] = [batch()], line = true) {
  const calls = installApi({ "GET /api/me": meRoute("ada"), ...extra });
  if (line) FakeSocket.greet("/line/ws", lineSnapshot());
  FakeSocket.greet("/ws/hops", hopSnapshot({ batches: rows.map((row) => ({ etag: '"v1"', batch: row })) }));
  renderApp("/dashboard");
  return calls;
}

beforeEach(() => useFakeSockets());

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("production line", () => {
  it("REQ-BT-026 the six counts are a grid of figures, each number above its label", async () => {
    dashboard({}, [batch()], false);
    FakeSocket.greet("/line/ws", lineSnapshot({ status: { ...LINE_STOPPED, started: 1234567890, errors: 3 } }));
    await screen.findByRole("heading", { name: "Production line" });
    const label = await screen.findByText("Started");
    expect(label.closest(".ds-key-values")).toHaveAttribute("data-figures", "true");
    expect(label.nextSibling).toHaveTextContent("1,234,567,890");
    for (const name of ["Closed", "Rejected", "Started per second", "Slots in use", "Throttled", "Walks dropped"]) expect(screen.getByText(name).closest(".ds-key-values")).toBe(label.closest(".ds-key-values"));
    expect(screen.getByRole("link", { name: "3 walks dropped, open the logs" })).toHaveAttribute("href", "/logs?level=error&source=line");
  });

  it("REQ-BT-026 no errors is still a link to the logs", async () => {
    dashboard();
    expect(await screen.findByRole("link", { name: "0 walks dropped, open the logs" })).toBeInTheDocument();
  });

  it("REQ-BT-026 lists every node the report names, the control plane included", async () => {
    dashboard();
    await act(async () => {
      FakeSocket.exact(MONITOR_WS).open();
      FakeSocket.exact(MONITOR_WS).send(REPORT);
    });
    const table = await screen.findByRole("table", { name: "Nodes" });
    expect(within(table).getByRole("row", { name: /Node1 Control plane/ })).toHaveTextContent("6,000 Mi");
    expect(within(table).getByRole("row", { name: /Node1 Control plane/ })).not.toHaveTextContent(" of ");
    expect(within(table).getByRole("row", { name: /k3s-node2 Worker/ })).toHaveTextContent("4,096 Mi of 8,192 Mi");
  });

  it("REQ-BT-042 changes show newest first as they arrive", async () => {
    dashboard();
    await act(async () => {
      FakeSocket.exact(MONITOR_WS).open();
      FakeSocket.exact(MONITOR_WS).send({ type: "changes", changes: [{ time: "2026-10-31T05:00:00Z", kind: "pod created", object: "batch-tracker/api-1", old: null, new: "Pending" }] });
    });
    expect(await screen.findByText("batch-tracker/api-1")).toBeInTheDocument();
    await act(async () => {
      FakeSocket.exact(MONITOR_WS).send({ type: "change", change: { time: "2026-10-31T05:01:00Z", kind: "pod deleted", object: "batch-tracker/api-2", old: "k3s-node2", new: null } });
    });
    const rows = within(await screen.findByRole("table", { name: "Changes" })).getAllByRole("row");
    expect(rows[1]).toHaveTextContent("batch-tracker/api-2");
    expect(rows[2]).toHaveTextContent("batch-tracker/api-1");
  });

  it("REQ-BT-042 changes say unknown when the monitor is lost, and empty when nothing changed", async () => {
    dashboard();
    await act(async () => FakeSocket.exact(MONITOR_WS).open());
    await act(async () => FakeSocket.exact(MONITOR_WS).send({ type: "changes", changes: [] }));
    expect(await screen.findByText("No changes yet")).toBeInTheDocument();
    await act(async () => FakeSocket.exact(MONITOR_WS).drop());
    expect(screen.queryByText("No changes yet")).not.toBeInTheDocument();
    expect(screen.getByText("Cluster work state unknown")).toBeInTheDocument();
  });

  it("REQ-BT-043 a change frame does not make an old report look fresh", async () => {
    dashboard();
    await act(async () => FakeSocket.exact(MONITOR_WS).open());
    await act(async () => FakeSocket.exact(MONITOR_WS).send({ type: "change", change: { time: "2026-10-31T05:01:00Z", kind: "pod deleted", object: "x", old: null, new: null } }));
    expect(screen.queryByRole("table", { name: "Nodes" })).not.toBeInTheDocument();
  });

  it("REQ-BT-031 start, change rate and stop", async () => {
    const push = (status: typeof LINE_STOPPED) => act(() => FakeSocket.find("/line/ws").send({ ...lineSnapshot({ status }), type: "line" }));
    const calls = dashboard({
      "POST /line": (_url, init) => {
        push({ ...LINE_STOPPED, running: true, rate: JSON.parse(String(init.body)).rate, started: 12, errors: 1 });
        return { status: 202 };
      },
      "DELETE /line": () => {
        push(LINE_STOPPED);
        return { body: LINE_STOPPED };
      },
    });
    await userEvent.click(await screen.findByRole("button", { name: "Start line" }));
    expect(await screen.findByText("Running, 1 per second requested")).toBeInTheDocument();
    expect(screen.getByText("Started").nextSibling).toHaveTextContent("12");
    expect(screen.getByText("Walks dropped").nextSibling).toHaveTextContent("1");
    const rate = screen.getByRole("slider", { name: "Requested rate" });
    fireEvent.input(rate, { target: { value: "3" } });
    fireEvent.change(rate);
    expect(await screen.findByText("Running, 3 per second requested")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Stop line" }));
    expect(await screen.findByText("Stopped")).toBeInTheDocument();
    expect(calls.filter((c) => c.url.endsWith("/line") && c.method !== "GET").map((c) => c.method)).toEqual(["POST", "POST", "DELETE"]);
    expect(calls.find((c) => c.method === "POST")?.headers.Authorization).toBeUndefined();
  });

  it("REQ-BT-031 stop says why it is unavailable when stopped", async () => {
    dashboard();
    expect(await screen.findByRole("button", { name: "Stop line" })).toHaveAccessibleDescription(/not running/);
  });

  it("REQ-BT-036 line service down says its state is unknown, with reconnect", async () => {
    dashboard({}, [batch()], false);
    await screen.findByRole("heading", { name: "Production line" });
    act(() => FakeSocket.find("/line/ws").drop());
    expect(await screen.findByText(/No state came from the line service/)).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Reconnect now" }).length).toBeGreaterThan(0);
    expect(screen.queryByText("Stopped")).toBeNull();
  });

  it("REQ-BT-031 a refused start is shown", async () => {
    dashboard({ "POST /line": { status: 403 } });
    await userEvent.click(await screen.findByRole("button", { name: "Start line" }));
    expect(await screen.findByText("You do not have access")).toBeInTheDocument();
  });
});

describe("nightly and failure", () => {
  it("REQ-BT-013 run nightly confirms with the time", async () => {
    const calls = dashboard({ "POST /api/compliance/run": { body: { flagged: 2 } } });
    await userEvent.click(await screen.findByRole("button", { name: "Run nightly now" }));
    expect(await screen.findByText(/Nightly run finished at/)).toBeInTheDocument();
    expect(calls.some((c) => c.url.endsWith("/compliance/run"))).toBe(true);
  });

  it("REQ-BT-036 a failed nightly run is shown with a way out", async () => {
    dashboard({ "POST /api/compliance/run": { status: 500 } });
    await userEvent.click(await screen.findByRole("button", { name: "Run nightly now" }));
    expect(await screen.findByText("The request failed")).toBeInTheDocument();
  });

  it("REQ-BT-011 failure button arms the chosen batch and links to it", async () => {
    const calls = dashboard({ "GET /api/batches/farm-a/b-1": { etag: '"v1"', body: batch() }, "POST /api/batches/farm-a/b-1/fail-next": {} });
    await userEvent.click(await screen.findByRole("button", { name: "Fail next delivery" }));
    expect(calls.some((c) => c.url.endsWith("/fail-next"))).toBe(false);
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Fail next delivery" }));
    expect(await screen.findByText("Set to fail: Bovine vaccine")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open this batch" })).toHaveAttribute("href", "/batches/farm-a/b-1");
    expect(calls.some((c) => c.url.endsWith("/fail-next"))).toBe(true);
  });

  async function armBatch() {
    const calls = dashboard({ "GET /api/batches/farm-a/b-1": { etag: '"v1"', body: batch() }, "POST /api/batches/farm-a/b-1/fail-next": {} });
    await userEvent.click(await screen.findByRole("button", { name: "Fail next delivery" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Fail next delivery" }));
    expect(await screen.findByText("Set to fail: Bovine vaccine")).toBeInTheDocument();
    return calls;
  }

  it("REQ-BT-011 the set-to-fail notice leaves once the batch blocks", async () => {
    await armBatch();
    const hops = FakeSocket.find("/ws/hops");
    act(() => hops.send({ type: "batch", etag: '"v2"', batch: batch({ version: 2, failNext: true }) }));
    expect(screen.getByText("Set to fail: Bovine vaccine")).toBeInTheDocument();
    act(() => hops.send({ type: "batch", etag: '"v3"', batch: batch({ version: 3, failNext: false, pending: { eventId: "e1", traceId: "t1", status: "dead", sentAt: null, error: "refused" } }) }));
    await waitFor(() => expect(screen.queryByText("Set to fail: Bovine vaccine")).not.toBeInTheDocument());
  });

  it("REQ-BT-011 the set-to-fail notice leaves when the batch changes state", async () => {
    await armBatch();
    act(() => FakeSocket.find("/ws/hops").send({ type: "batch", etag: '"v2"', batch: batch({ version: 2, failNext: true, state: "Dose2" }) }));
    await waitFor(() => expect(screen.queryByText("Set to fail: Bovine vaccine")).not.toBeInTheDocument());
  });

  it("REQ-BT-011 the confirm dialog says why Cancel is locked while it saves", async () => {
    dashboard({ "GET /api/batches/farm-a/b-1": () => new Promise<Reply>(() => undefined) });
    await userEvent.click(await screen.findByRole("button", { name: "Fail next delivery" }));
    const dialog = screen.getByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Fail next delivery" }));
    expect(await within(dialog).findByText("Cancel is locked while this runs. It opens when the server answers, or after 65 seconds.")).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Cancel" })).toHaveAttribute("aria-disabled", "true");
  });

  it("REQ-BT-036 no eligible batch explains itself", async () => {
    dashboard({}, [batch({ state: "Closed" })]);
    expect(await screen.findByText("No batch can take a failure")).toBeInTheDocument();
  });
});

describe("REQ-BT-050 a lost write on a Dashboard panel", () => {
  it("REQ-BT-050 a lost failure injection offers Dismiss: this panel has no batch page to reload", async () => {
    dashboard({ "GET /api/batches/farm-a/b-1": { etag: '"v1"', body: batch() }, "POST /api/batches/farm-a/b-1/fail-next": () => Promise.reject(new TypeError("Failed to fetch")) });
    await userEvent.click(await screen.findByRole("button", { name: "Fail next delivery" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Fail next delivery" }));
    expect(await screen.findByText("The result is unknown")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Dismiss" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Reload batch" })).not.toBeInTheDocument();
  });

  it("REQ-BT-050 the nightly run offers Dismiss and no reload it cannot do", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "POST /api/compliance/run": () => Promise.reject(new TypeError("Failed to fetch")) });
    renderApp("/dashboard");
    await userEvent.click(await screen.findByRole("button", { name: "Run nightly now" }));
    expect(await screen.findByText("The result is unknown")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Dismiss" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Reload batch" })).not.toBeInTheDocument();
  });
});
