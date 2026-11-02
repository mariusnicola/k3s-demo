import { vi } from "vitest";
import { parseMonitorFrame } from "../feed/hops";
import { BUCKETS, DEFAULT_BUCKET, MAX_BUCKETS, METRICS, addPoint, parseMetricPoint, readBucket, storeBucket, toView } from "./metrics";

const AT = (minute: number) => new Date(Date.UTC(2026, 9, 31, 10, minute)).toISOString();
const series = (values: (number | null)[], name = "bt_events_created_total", start = 0, step = 1) => ({
  name,
  total: values.reduce<number>((sum, v) => sum + (v ?? 0), 0),
  averagePerMinute: 0,
  points: values.map((value, i) => ({ at: AT(start + i * step), value })),
});
const reply = (values: (number | null)[], bucket = "1m", step = 1) => ({ bucket, available: true, error: null, historyStart: AT(0), series: [series(values, "bt_events_created_total", 0, step)] });

afterEach(() => vi.restoreAllMocks());

describe("REQ-BT-046 bucket choice", () => {
  it("REQ-BT-046 the buckets are the seven asked for, 10s by default, 120 at most", () => {
    expect(BUCKETS).toEqual(["10s", "30s", "1m", "30m", "1h", "3h", "1d"]);
    expect(DEFAULT_BUCKET).toBe("10s");
    expect(MAX_BUCKETS).toBe(120);
  });

  it("REQ-BT-046 TM-2151 a stored bucket comes back", () => {
    storeBucket("3h");
    expect(readBucket()).toBe("3h");
  });

  it("REQ-BT-046 TM-2157 a stored bucket not in the list gives the default", () => {
    localStorage.setItem("bt.metricsBucket", "2w");
    expect(readBucket()).toBe("10s");
  });

  it("REQ-BT-046 TM-2156 blocked storage gives the default and does not throw", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    expect(() => storeBucket("1h")).not.toThrow();
    expect(readBucket()).toBe("10s");
  });
});

describe("REQ-BT-046 metric names", () => {
  it("REQ-BT-046 every tile the owner asked for has a name, and a chart group of at most four lines", () => {
    expect(METRICS.map((m) => m.label)).toEqual(["Events created", "Events passed", "Batches created", "Dose 1", "Dose 2", "Inspections passed", "Inspections failed", "Shipped", "Closed", "Rejected"]);
    expect(METRICS.map((m) => m.name)).toEqual([
      "bt_events_created_total",
      "bt_events_passed_total",
      "bt_batches_created_total",
      "bt_dose1_total",
      "bt_dose2_total",
      "bt_inspections_passed_total",
      "bt_inspections_failed_total",
      "bt_batches_shipped_total",
      "bt_batches_closed_total",
      "bt_batches_rejected_total",
    ]);
    const groups = new Map<string, number>();
    for (const m of METRICS) groups.set(m.group, (groups.get(m.group) ?? 0) + 1);
    expect(Math.max(...groups.values())).toBeLessThanOrEqual(4);
  });
});

describe("REQ-BT-046 the metric point frame", () => {
  it("REQ-BT-046 a good frame parses and anything else is refused", () => {
    expect(parseMetricPoint({ type: "metricPoint", at: AT(1), values: { a: 1 } })).toEqual({ at: AT(1), values: { a: 1 } });
    expect(parseMetricPoint({ type: "metricPoint", at: "nope", values: {} })).toBeNull();
    expect(parseMetricPoint({ type: "metricPoint", at: AT(1) })).toBeNull();
  });

  it("REQ-BT-046 a value that is not a number is left out, so it reads unknown", () => {
    expect(parseMetricPoint({ type: "metricPoint", at: AT(1), values: { a: 1, b: "2" } })).toEqual({ at: AT(1), values: { a: 1 } });
  });

  it("REQ-BT-046 the monitor socket carries it as its own frame", () => {
    const frame = parseMonitorFrame(JSON.stringify({ type: "metricPoint", at: AT(1), values: { a: 1 } }));
    expect(frame).toEqual({ type: "metricPoint", point: { at: AT(1), values: { a: 1 } } });
    expect(parseMonitorFrame(JSON.stringify({ type: "metricPoint", at: "nope", values: {} }))).toBeNull();
  });
});

describe("REQ-BT-046 live points", () => {
  it("REQ-BT-046 TM-2152 a point at 1m adds a bucket and moves the total and average", () => {
    const view = addPoint(toView(reply([2, 2]), "1m"), { at: AT(2), values: { bt_events_created_total: 5 } });
    expect(view.series[0]!.points.map((p) => p.value)).toEqual([2, 2, 5]);
    expect(view.series[0]!.total).toBe(9);
    expect(view.series[0]!.averagePerMinute).toBe(3);
  });

  it("REQ-BT-046 a repeated point is not counted twice", () => {
    const once = addPoint(toView(reply([2]), "1m"), { at: AT(1), values: { bt_events_created_total: 5 } });
    expect(addPoint(once, { at: AT(1), values: { bt_events_created_total: 5 } })).toBe(once);
  });

  it("REQ-BT-046 a point the first read already holds is not counted twice", () => {
    const first = toView(reply([2, 2]), "1m");
    expect(addPoint(first, { at: AT(1), values: { bt_events_created_total: 2 } })).toBe(first);
  });

  it("REQ-BT-046 TM-2159 at a wider view the point joins the open bucket", () => {
    const start = toView(reply([10, 20], "30m", 30), "30m");
    const once = addPoint(addPoint(start, { at: AT(60), values: { bt_events_created_total: 1 } }), { at: AT(61), values: { bt_events_created_total: 2 } });
    expect(once.series[0]!.points.map((p) => p.value)).toEqual([10, 20, 3]);
    expect(once.series[0]!.points.at(-1)!.at).toBe(AT(60));
  });

  it("REQ-BT-046 missed minutes read as unknown, never joined across and never zero", () => {
    const view = addPoint(toView(reply([2]), "1m"), { at: AT(3), values: { bt_events_created_total: 4 } });
    expect(view.series[0]!.points.map((p) => p.value)).toEqual([2, null, null, 4]);
  });

  it("REQ-BT-046 a series the point leaves out reads unknown for that bucket", () => {
    const view = addPoint(toView(reply([2]), "1m"), { at: AT(1), values: {} });
    expect(view.series[0]!.points.at(-1)!.value).toBeNull();
    expect(view.series[0]!.total).toBe(2);
  });

  it("REQ-BT-046 the chart never holds more than 120 buckets", () => {
    const view = addPoint(toView(reply(Array.from({ length: 120 }, () => 1)), "1m"), { at: AT(120), values: { bt_events_created_total: 1 } });
    expect(view.series[0]!.points).toHaveLength(120);
  });
});

describe("addPoint leaves what is no counter alone", () => {
  const gauge = { name: "a-peak-not-a-count", total: 0, averagePerMinute: 0, points: [{ at: AT(0), value: 7 }] };
  const counter = { name: "bt_events_created_total", total: 2, averagePerMinute: 2, points: [{ at: AT(0), value: 2 }] };
  const view = (bucket: "1m" | "1h") => toView({ bucket, available: true, error: null, historyStart: AT(0), series: [gauge, counter] }, bucket);

  it("REQ-BT-026 a live point never sums into a series that is no counter", () => {
    const values = { "a-peak-not-a-count": 9, bt_events_created_total: 1 };
    const next = addPoint(addPoint(view("1h"), { at: AT(60), values }), { at: AT(61), values });
    expect(next.series[0]).toBe(gauge);
    expect(next.series[1]!.points.map((p) => p.value)).toEqual([2, 2]);
  });

  it("REQ-BT-026 a live point adds no unknown bucket to a series that refreshes on read", () => {
    const next = addPoint(view("1m"), { at: AT(1), values: { bt_events_created_total: 1 } });
    expect(next.series[0]!.points).toHaveLength(1);
    expect(next.series[1]!.points).toHaveLength(2);
  });
});
