import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { MONITOR_WS } from "../../../config";
import { hopSnapshot, lineSnapshot } from "../../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Handler, type Reply } from "../../../mocks/harness";

const AT = (minute: number) => new Date(Date.UTC(2026, 9, 31, 10, minute)).toISOString();
const NAMES = ["bt_events_created_total", "bt_events_passed_total", "bt_batches_created_total", "bt_dose1_total", "bt_dose2_total", "bt_inspections_passed_total", "bt_inspections_failed_total", "bt_batches_shipped_total", "bt_batches_closed_total", "bt_batches_rejected_total"];
const series = (values: (number | null)[], name: string) => ({ name, total: values.reduce<number>((s, v) => s + (v ?? 0), 0), averagePerMinute: 1.5, points: values.map((value, i) => ({ at: AT(i), value })) });
const body = (bucket = "1m", values: (number | null)[] = [1, 2, 3]) => ({ bucket, available: true, error: null, historyStart: AT(0), series: NAMES.map((n) => series(values, n)) });

function open(reply: Reply | Handler = { body: body() }) {
  const calls = installApi({ "GET /api/me": meRoute("ada"), "GET /metrics/series": reply });
  FakeSocket.greet("/ws/hops", hopSnapshot());
  FakeSocket.greet("/line/ws", lineSnapshot());
  renderApp("/dashboard");
  return calls;
}
const reads = (calls: ReturnType<typeof installApi>) => calls.filter((c) => c.url.includes("/metrics/series"));
const panel = async () => (await screen.findByRole("heading", { name: "Performance" })).closest("section")!;
const monitor = () => {
  const socket = FakeSocket.exact(MONITOR_WS);
  act(() => socket.open());
  return socket;
};

beforeEach(() => {
  useFakeSockets();
  localStorage.setItem("bt.metricsBucket", "1m");
});
afterEach(() => void vi.unstubAllGlobals());

describe("REQ-BT-046 tiles", () => {
  it("REQ-BT-046 TM-2149 each measure shows its total and average per minute", async () => {
    open();
    const section = await panel();
    await within(section).findByRole("img", { name: /Events/ });
    for (const label of ["Events created", "Events passed", "Batches created", "Dose 1", "Dose 2", "Inspections passed", "Inspections failed", "Shipped", "Closed", "Rejected"]) {
      const tile = within(section).getAllByText(label)[0]!.closest(".ds-stat")!;
      expect(tile).toHaveTextContent("6");
      expect(tile).toHaveTextContent("1.5 per minute");
    }
  });

  it("REQ-BT-046 a measure the service did not send reads unknown, never zero", async () => {
    open({ body: { ...body(), series: [series([1], "bt_events_created_total")] } });
    const section = await panel();
    await within(section).findByRole("img", { name: /Events/ });
    expect(within(section).getAllByText("Rejected")[0]!.closest(".ds-stat")).toHaveTextContent("Unknown");
  });
});

describe("REQ-BT-046 all unknown", () => {
  it("REQ-BT-046 TM-2140 a measure with every bucket unknown reads unknown, never a total of zero", async () => {
    open({ body: body("1m", [null, null]) });
    const section = await panel();
    await within(section).findByRole("img", { name: /Events/ });
    const tile = within(section).getAllByText("Rejected")[0]!.closest(".ds-stat")!;
    expect(tile).toHaveTextContent("Unknown");
    expect(tile).not.toHaveTextContent("per minute");
  });
});

describe("REQ-BT-046 selector", () => {
  it("REQ-BT-046 TM-2150 the first read asks for the stored bucket, and each choice reads that bucket once", async () => {
    const calls = open((url) => ({ body: body(new URL(url, "http://x").searchParams.get("bucket")!) }));
    await within(await panel()).findByRole("img", { name: /Events/ });
    expect(reads(calls)).toHaveLength(1);
    expect(reads(calls)[0]!.url).toContain("bucket=1m");
    for (const label of ["10s", "30s", "30m", "1h", "3h", "1d"]) {
      await userEvent.click(screen.getByRole("radio", { name: label }));
      await waitFor(() => expect(reads(calls).at(-1)!.url).toContain(`bucket=${label}`));
    }
    expect(reads(calls)).toHaveLength(7);
  });

  it("REQ-BT-046 a new bucket shows a loading state while it is read, never the old charts or a read time of never", async () => {
    let release: (reply: Reply) => void = () => undefined;
    const calls = open((url) => {
      const bucket = new URL(url, "http://x").searchParams.get("bucket")!;
      return bucket === "1m" ? { body: body("1m") } : new Promise<Reply>((resolve) => (release = resolve));
    });
    const section = await panel();
    await within(section).findByRole("img", { name: /Events/ });
    await userEvent.click(screen.getByRole("radio", { name: "30s" }));
    await waitFor(() => expect(reads(calls)).toHaveLength(2));
    expect(within(section).getByRole("status", { name: "Loading performance" })).toBeInTheDocument();
    expect(within(section).queryByRole("img", { name: /Events/ })).not.toBeInTheDocument();
    expect(section).not.toHaveTextContent("never");
    await act(async () => release({ body: body("30s") }));
    expect(await within(section).findByRole("img", { name: /Events/ })).toBeInTheDocument();
  });

  it("REQ-BT-046 TM-2151 the choice is remembered after a reload", async () => {
    const calls = open();
    await within(await panel()).findByRole("img", { name: /Events/ });
    await userEvent.click(screen.getByRole("radio", { name: "3h" }));
    expect(localStorage.getItem("bt.metricsBucket")).toBe("3h");
    await waitFor(() => expect(reads(calls)).toHaveLength(2));
  });
});

describe("REQ-BT-046 no polling", () => {
  it("REQ-BT-046 one read on load, and a metric point adds to the tile with no second read", async () => {
    const calls = open();
    const section = await panel();
    await within(section).findByRole("img", { name: /Events/ });
    act(() => monitor().send({ type: "metricPoint", at: AT(3), values: Object.fromEntries(NAMES.map((n) => [n, 4])) }));
    await waitFor(() => expect(within(section).getAllByText("Rejected")[0]!.closest(".ds-stat")).toHaveTextContent("10"));
    expect(reads(calls)).toHaveLength(1);
  });

  it("REQ-BT-046 a point that lands during the first read is kept", async () => {
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => (release = resolve));
    open(async () => {
      await gate;
      return { body: body() };
    });
    const section = await panel();
    act(() => monitor().send({ type: "metricPoint", at: AT(3), values: Object.fromEntries(NAMES.map((n) => [n, 4])) }));
    await act(async () => release());
    await waitFor(() => expect(within(section).getAllByText("Rejected")[0]!.closest(".ds-stat")).toHaveTextContent("10"));
  });
});

describe("REQ-BT-046 states", () => {
  it("REQ-BT-046 TM-2158 loading says so", async () => {
    open(() => new Promise<Reply>(() => undefined));
    expect(await screen.findByRole("status", { name: "Loading performance" })).toBeInTheDocument();
  });

  it("REQ-BT-046 TM-2158 empty says so", async () => {
    open({ body: { ...body(), series: [] } });
    expect(await screen.findByText("No performance data yet")).toBeInTheDocument();
  });

  it("REQ-BT-046 TM-2153 503 says why, with no tile and no zero", async () => {
    open({ status: 503, body: { bucket: "1m", available: false, error: "Prometheus did not answer", historyStart: null, series: [] } });
    const section = await panel();
    expect(await within(section).findByText("Prometheus did not answer")).toBeInTheDocument();
    expect(section.querySelector(".ds-stat")).toBeNull();
    expect(within(section).getByRole("button", { name: "Try again" })).toBeInTheDocument();
  });

  it("REQ-BT-046 TM-2154 a failed read keeps the last series and says so", async () => {
    let fail = false;
    open(() => (fail ? { status: 400 } : { body: body() }));
    const section = await panel();
    await within(section).findByRole("img", { name: /Events/ });
    fail = true;
    await userEvent.click(screen.getByRole("radio", { name: "1h" }));
    expect(await within(section).findByText(/Showing the last series read/)).toBeInTheDocument();
    expect(within(section).getAllByText("Rejected").length).toBeGreaterThan(0);
  });

  it("REQ-BT-046 TM-2155 a null point is a gap in the line and says unknown", async () => {
    open({ body: body("1m", [1, null, 3, 4]) });
    const section = await panel();
    await within(section).findByRole("img", { name: /Events/ });
    const chart = within(section).getByRole("img", { name: /Events/ });
    expect(chart.querySelectorAll("path[data-series='0']")).toHaveLength(1);
    expect(chart.querySelectorAll("circle[data-series='0']")).toHaveLength(1);
    expect(within(section).getAllByText(/Gaps are unknown buckets, not zero/).length).toBeGreaterThan(0);
  });

  it("REQ-BT-046 partial history says where the data starts", async () => {
    open({ body: { ...body("1m", [null, 2, 3]), historyStart: AT(1) } });
    expect(await within(await panel()).findByText(/History starts/)).toBeInTheDocument();
  });

  it("REQ-BT-046 TM-2160 a lost monitor socket says updates are lost and keeps the chart", async () => {
    open();
    const section = await panel();
    await within(section).findByRole("img", { name: /Events/ });
    act(() => FakeSocket.exact(MONITOR_WS).drop());
    expect(await within(section).findByText("Live updates are lost")).toBeInTheDocument();
    expect(within(section).getByRole("img", { name: /Events/ })).toBeInTheDocument();
  });

  it("REQ-BT-046 TM-2237 a non admin reads no series", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia") });
    renderApp("/dashboard");
    await screen.findByRole("heading", { name: "Batches by step" });
    expect(reads(calls)).toEqual([]);
  });
});

describe("REQ-BT-046 reading a chart", () => {
  it("REQ-BT-046 hover shows every series at that bucket", async () => {
    open();
    const section = await panel();
    await within(section).findByRole("img", { name: /Events/ });
    const chart = within(section).getByRole("img", { name: /Events/ });
    fireEvent.pointerMove(chart, { clientX: 0 });
    expect(within(chart.parentElement!).getByRole("tooltip")).toHaveTextContent("Events passed");
    fireEvent.pointerLeave(chart);
    expect(within(chart.parentElement!).queryByRole("tooltip")).toBeNull();
  });

  it("REQ-BT-046 TM-2161 a keyboard walks the buckets", async () => {
    open();
    const section = await panel();
    await within(section).findByRole("img", { name: /Events/ });
    const chart = within(section).getByRole("img", { name: /Events/ });
    act(() => chart.focus());
    expect(chart).toHaveFocus();
    fireEvent.keyDown(chart, { key: "End" });
    expect(within(chart.parentElement!).getByRole("tooltip")).toHaveTextContent("3");
    fireEvent.keyDown(chart, { key: "Home" });
    expect(within(chart.parentElement!).getByRole("tooltip")).toHaveTextContent("1");
  });

  it("REQ-BT-046 TM-2161 every value is reachable in a table, null as Unknown", async () => {
    open({ body: body("1m", [1, null]) });
    const section = await panel();
    await within(section).findByRole("img", { name: /Events/ });
    await userEvent.click(within(section).getByRole("button", { name: "Show values, Events" }));
    expect(within(within(section).getByRole("table", { name: "Events values" })).getAllByText("Unknown").length).toBe(2);
  });
});
