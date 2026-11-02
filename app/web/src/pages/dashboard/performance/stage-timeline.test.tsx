import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MONITOR_WS } from "../../../config";
import { hopSnapshot, lineSnapshot } from "../../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Handler, type Reply } from "../../../mocks/harness";

const AT = (i: number) => new Date(Date.UTC(2026, 10, 2, 10, 0, i * 10)).toISOString();
const NAMES = ["bt_events_created_total", "bt_batches_created_total", "bt_batches_closed_total"];
const series = (name: string, values: (number | null)[]) => ({ name, total: 1, averagePerMinute: 1, points: values.map((value, i) => ({ at: AT(i), value })) });
const stage = (name: string, p95s: (number | null)[]) => ({ stage: name, points: p95s.map((p95, i) => ({ at: AT(i), p50: p95, p95 })) });
const body = (stages: (number | null)[][] = [[0.01, 0.02], [0.005, 0.006], [0.2, 8], [0.1, 0.9]], pending: (number | null)[] = [3, 500]) => ({
  bucket: "10s",
  available: true,
  error: null,
  historyStart: AT(0),
  series: NAMES.map((n) => series(n, [70, 80])),
  stages: ["api", "store", "queue", "worker"].map((name, i) => stage(name, stages[i]!)),
  pending: pending.map((value, i) => ({ at: AT(i), value })),
});

function open(reply: Reply | Handler = { body: body() }) {
  const calls = installApi({ "GET /api/me": meRoute("ada"), "GET /metrics/series": reply });
  FakeSocket.greet("/ws/hops", hopSnapshot());
  FakeSocket.greet("/line/ws", lineSnapshot());
  renderApp("/dashboard");
  return calls;
}
const reads = (calls: ReturnType<typeof installApi>) => calls.filter((c) => c.url.includes("/metrics/series"));
const panel = async () => (await screen.findByRole("heading", { name: "Performance" })).closest("section")!;
const stat = (section: HTMLElement, label: string) => within(section).getAllByText(label).find((el) => el.classList.contains("ds-stat-label"))!.closest(".ds-stat")!;

const monitor = () => {
  const socket = FakeSocket.exact(MONITOR_WS);
  act(() => socket.open());
  return socket;
};
const frame = (i: number, p95s: (number | null)[], pending: number | null = 3) => ({ type: "stagePoint", at: AT(i), stages: ["api", "store", "queue", "worker"].map((name, n) => ({ stage: name, p50: p95s[n], p95: p95s[n] })), pending, values: { bt_batches_created_total: 90, bt_batches_closed_total: 50 } });
const values = (section: HTMLElement, name: RegExp) => within(section).getByRole("img", { name });

beforeEach(() => {
  useFakeSockets();
  localStorage.clear();
});

describe("where the time goes", () => {
  it("REQ-BT-059 the first read asks for 10 s buckets", async () => {
    const calls = open();
    await panel();
    expect(reads(calls)[0]!.url).toContain("bucket=10s");
  });

  it("REQ-BT-059 the slowest stage now and the worst bucket are named in words", async () => {
    open({ body: body([[0.01, 0.02], [0.005, 0.006], [8, 0.2], [0.1, 0.9]]) });
    const section = await panel();
    await within(section).findByText("Slowest now");
    expect(stat(section, "Slowest now")).toHaveTextContent("Worker handling");
    expect(stat(section, "Slowest now")).toHaveTextContent("900 ms p95");
    expect(stat(section, "Worst on screen")).toHaveTextContent("Waiting in the queue");
    expect(stat(section, "Worst on screen")).toHaveTextContent("8.0 s p95");
  });

  it("REQ-BT-059 three charts stand on one time axis", async () => {
    open();
    const section = await panel();
    for (const name of [/^Batches per second, 2 buckets/, /^Stage p95, 2 buckets/, /^Pending peak, 2 buckets/]) expect(await within(section).findByRole("img", { name })).toBeInTheDocument();
    expect(within(section).getByText("Batches per second")).toBeInTheDocument();
    expect(within(section).getByText("Stage p95")).toBeInTheDocument();
    expect(within(section).getAllByText("Pending peak")).toHaveLength(2);
  });

  it("REQ-BT-059 the batches, pending and each stage read as figures", async () => {
    open();
    const section = await panel();
    await within(section).findByText("Slowest now");
    expect(stat(section, "Started per second")).toHaveTextContent("8.0");
    expect(stat(section, "Closed per second")).toHaveTextContent("8.0");
    expect(stat(section, "Pending peak")).toHaveTextContent("500");
    expect(stat(section, "API call")).toHaveTextContent("20 ms");
    expect(stat(section, "Waiting in the queue")).toHaveTextContent("8.0 s");
  });

  it("REQ-BT-059 a p95 at the ceiling reads as 60 s or more", async () => {
    open({ body: body([[0.01], [0.01], [60], [0.1]], [1]) });
    const section = await panel();
    await within(section).findByText("Slowest now");
    expect(stat(section, "Slowest now")).toHaveTextContent(">= 60 s p95");
  });

  it("REQ-BT-059 a stage with no data says so, and never zero", async () => {
    open({ body: body([[0.01, 0.02], [null, null], [0.2, 8], [0.1, 0.9]]) });
    const section = await panel();
    await within(section).findByText("Slowest now");
    expect(stat(section, "Database write")).toHaveTextContent("No data");
    expect(stat(section, "Database write")).not.toHaveTextContent("0 ms");
    expect(within(section).getByText("No data in this view: Database write.")).toBeInTheDocument();
  });

  it("REQ-BT-059 a gap in a stage reads as unknown, not zero", async () => {
    open({ body: body([[0.01, null], [0.005, 0.006], [0.2, 8], [0.1, 0.9]]) });
    const section = await panel();
    await within(section).findByText("Slowest now");
    expect(within(section).getByText("Gaps are unknown buckets, not zero.")).toBeInTheDocument();
  });

  it("REQ-BT-059 no samples at any stage name no stage", async () => {
    open({ body: body([[null], [null], [null], [null]], [null]) });
    const section = await panel();
    await within(section).findByText("No stage timing in this view");
    expect(within(section).queryByText("Slowest now")).toBeNull();
    expect(within(section).queryByRole("img", { name: /^Stage p95/ })).toBeNull();
    expect(stat(section, "Pending peak")).toHaveTextContent("No data");
  });

  it("REQ-BT-059 a Monitor that sends no stages says stage timing is not reported, and the rest of the card stands", async () => {
    const { stages: _stages, pending: _pending, ...old } = body();
    open({ body: old });
    const section = await panel();
    expect(await within(section).findByText("Stage timing is not reported")).toBeInTheDocument();
    expect(within(section).getByRole("img", { name: /^Batches/ })).toBeInTheDocument();
  });

  it("REQ-BT-059 the card says what moves live", async () => {
    open();
    const section = await panel();
    await within(section).findByText("Slowest now");
    expect(within(section).getByText("Batches, stages and pending arrive live, one bucket every 10 s.")).toBeInTheDocument();
  });

  it("REQ-BT-059 Prometheus down shows the problem, with no stage figures", async () => {
    open({ status: 503, body: { bucket: "10s", available: false, error: "Prometheus is down", historyStart: null, series: [], stages: [], pending: [] } });
    const section = await panel();
    expect((await within(section).findAllByText(/could not|request failed|Prometheus|unavailable/i)).length).toBeGreaterThan(0);
    expect(within(section).queryByText("Slowest now")).toBeNull();
    expect(within(section).queryByText("0 ms")).toBeNull();
  });

  it("REQ-BT-059 a 503 is the Monitor's answer: it shows at once, and Try again recovers", async () => {
    let down = true;
    const calls = open(() => (down ? { status: 503, body: { bucket: "10s", available: false, error: "Prometheus is down", historyStart: null, series: [], stages: [], pending: [] } } : { body: body() }));
    const section = await panel();
    const again = await within(section).findByRole("button", { name: "Try again" });
    expect(reads(calls)).toHaveLength(1);
    down = false;
    await userEvent.click(again);
    expect(await within(section).findByText("Slowest now")).toBeInTheDocument();
  });
});

describe("a failed read of a new bucket", () => {
  it("REQ-BT-059 keeps the old view and says it is from an earlier read, never a read time of never", async () => {
    localStorage.setItem("bt.metricsBucket", "30s");
    open((url) => (new URL(url, "http://x").searchParams.get("bucket") === "30s" ? { body: { ...body(), bucket: "30s" } } : { status: 500 }));
    const section = await panel();
    await within(section).findByText("Slowest now");
    await userEvent.click(screen.getByRole("radio", { name: "1m" }));
    expect(await within(section).findByText(/Showing the last series read/)).toBeInTheDocument();
    expect(section).not.toHaveTextContent("never");
    expect(within(section).getByText(/from an earlier read/)).toBeInTheDocument();
  });
});

describe("the live frame", () => {
  it("REQ-BT-059 a frame adds one bucket to every chart, with no read", async () => {
    const calls = open();
    const section = await panel();
    await within(section).findByText("Slowest now");
    const first = reads(calls).length;
    act(() => monitor().send(frame(2, [0.03, 0.007, 0.3, 2])));
    expect(values(section, /^Stage p95, 3 buckets/)).toBeInTheDocument();
    expect(values(section, /^Pending peak, 3 buckets/)).toBeInTheDocument();
    expect(values(section, /^Batches per second, 3 buckets/)).toBeInTheDocument();
    expect(reads(calls).length).toBe(first);
  });

  it("REQ-BT-059 a frame moves the slowest stage and the figures", async () => {
    open();
    const section = await panel();
    await within(section).findByText("Slowest now");
    act(() => monitor().send(frame(2, [0.03, 0.007, 0.3, 2], 40)));
    expect(stat(section, "Slowest now")).toHaveTextContent("Worker handling");
    expect(stat(section, "Slowest now")).toHaveTextContent("2.0 s p95");
    expect(stat(section, "Pending peak")).toHaveTextContent("40");
    expect(stat(section, "Started per second")).toHaveTextContent("9.0");
    expect(stat(section, "Closed per second")).toHaveTextContent("5.0");
    expect(stat(section, "Batches created")).toHaveTextContent("240");
  });

  it("REQ-BT-059 a frame during the first read is kept", async () => {
    let answer: (reply: Reply) => void = () => undefined;
    open(() => new Promise<Reply>((resolve) => (answer = resolve)));
    const section = await panel();
    await waitFor(() => expect(FakeSocket.exact(MONITOR_WS)).toBeDefined());
    act(() => monitor().send(frame(2, [0.03, 0.007, 0.3, 2])));
    await act(async () => answer({ body: body() }));
    expect(await within(section).findByRole("img", { name: /^Stage p95, 3 buckets/ })).toBeInTheDocument();
  });

  it("REQ-BT-059 the 30 s view says all of it is as read", async () => {
    localStorage.setItem("bt.metricsBucket", "30s");
    open();
    const section = await panel();
    await within(section).findByText("Slowest now");
    expect(within(section).getByText(/^This view is as read at .+\. It does not move live\.$/)).toBeInTheDocument();
  });

  it("REQ-BT-059 a view wider than 10 s is left as read", async () => {
    localStorage.setItem("bt.metricsBucket", "1m");
    open();
    const section = await panel();
    await within(section).findByText("Slowest now");
    act(() => monitor().send(frame(2, [0.03, 0.007, 0.3, 2])));
    expect(values(section, /^Stage p95, 2 buckets/)).toBeInTheDocument();
    expect(within(section).getByText(/^Stages and pending last read .+\. They move live only at 10s buckets\.$/)).toBeInTheDocument();
  });
});
