import { parseMonitorFrame } from "../feed/hops";
import { addPoint, toView, type MetricsReply } from "./metrics";
import { MAX_BUCKETS, addBucket } from "./metrics";
import { STAGES, addStagePoint, formatDuration, parseStagePoint, stageView } from "./stages";

const AT = (i: number) => new Date(Date.UTC(2026, 10, 2, 10, 0, i * 10)).toISOString();
const stage = (name: string, p95s: (number | null)[]) => ({ stage: name, points: p95s.map((p95, i) => ({ at: AT(i), p50: p95 === null ? null : p95 / 2, p95 })) });
const counter = (name: string, values: (number | null)[]) => ({ name, total: 0, averagePerMinute: 0, points: values.map((value, i) => ({ at: AT(i), value })) });

function reply(over: Partial<MetricsReply> = {}): MetricsReply {
  return {
    bucket: "10s",
    available: true,
    error: null,
    historyStart: AT(0),
    series: [counter("bt_batches_created_total", [70, 80, null]), counter("bt_batches_closed_total", [60, null, 20])],
    stages: [stage("api", [0.01, 0.02, 0.01]), stage("store", [0.005, 0.005, null]), stage("queue", [0.2, 8, 0.4]), stage("worker", [0.1, 0.1, 0.9])],
    pending: [
      { at: AT(0), value: 3 },
      { at: AT(1), value: 500 },
      { at: AT(2), value: null },
    ],
    ...over,
  };
}

const view = (over?: Partial<MetricsReply>) => stageView(toView(reply(over), "10s"));

describe("stageView", () => {
  it("REQ-BT-059 the stages keep their order and read in plain words", () => {
    expect(STAGES.map((s) => s.label)).toEqual(["API call", "Database write", "Waiting in the queue", "Worker handling"]);
    expect(view()!.p95.map((s) => s.label)).toEqual(["API call", "Database write", "Waiting in the queue", "Worker handling"]);
  });

  it("REQ-BT-059 only p95 is drawn, and every chart shares the same times", () => {
    const stages = view()!;
    expect(stages.buckets).toEqual([AT(0), AT(1), AT(2)]);
    expect(stages.p95[2]!.points.map((p) => p.value)).toEqual([0.2, 8, 0.4]);
  });

  it("REQ-BT-059 started and closed are per second of the bucket, and unknown stays unknown", () => {
    const stages = view()!;
    expect(stages.started.map((p) => p.value)).toEqual([7, 8, null]);
    expect(stages.closed.map((p) => p.value)).toEqual([6, null, 2]);
  });

  it("REQ-BT-059 a 30 s bucket divides by 30", () => {
    const stages = stageView(toView(reply({ bucket: "30s" }), "30s"))!;
    expect(stages.started[0]!.value).toBeCloseTo(70 / 30, 10);
  });

  it("REQ-BT-059 slowest now is the highest p95 of the latest bucket with any", () => {
    expect(view()!.now).toEqual({ label: "Worker handling", seconds: 0.9, at: AT(2) });
  });

  it("REQ-BT-059 worst on screen is the highest p95 of any bucket, with its time", () => {
    expect(view()!.worst).toEqual({ label: "Waiting in the queue", seconds: 8, at: AT(1) });
  });

  it("REQ-BT-059 a latest bucket with no samples does not read as nothing slow", () => {
    const stages = view({ stages: [stage("api", [0.1, null]), stage("store", [null, null]), stage("queue", [0.3, null]), stage("worker", [null, null])] })!;
    expect(stages.now).toEqual({ label: "Waiting in the queue", seconds: 0.3, at: AT(0) });
  });

  it("REQ-BT-059 with no samples at all nothing is named slowest", () => {
    const stages = view({ stages: STAGES.map((s) => stage(s.stage, [null, null])) })!;
    expect(stages.now).toBeNull();
    expect(stages.worst).toBeNull();
  });

  it("REQ-BT-059 each stage says its latest p95, or none, never zero", () => {
    const stages = view({ stages: [stage("api", [0.01, null]), stage("store", [null, null]), stage("queue", [0, null]), stage("worker", [0.1, 0.2])] })!;
    expect(stages.p95.map((s) => s.latest)).toEqual([0.01, null, 0, 0.2]);
  });

  it("REQ-BT-059 the latest figures skip unknown buckets", () => {
    const stages = view()!;
    expect(stages.latestStarted).toBe(8);
    expect(stages.latestClosed).toBe(2);
    expect(stages.latestPending).toBe(500);
  });

  it.each([[{ stages: [] }], [{ stages: undefined }]])("REQ-BT-059 a reply with no stages gives no stage view: %j", (over) => {
    expect(view(over)).toBeNull();
  });

  it("REQ-BT-059 a live point leaves the stages and the pending as read", () => {
    const start = toView(reply({ bucket: "1m" }), "1m");
    const next = addPoint(start, { at: AT(600), values: { bt_batches_created_total: 1 } });
    expect(next.stages).toBe(start.stages);
    expect(next.pending).toBe(start.pending);
  });
});

describe("a bucket under a minute", () => {
  it("REQ-BT-059 a live point, a whole minute, never lands in a 10 s bucket", () => {
    const start = toView(reply(), "10s");
    expect(addPoint(start, { at: AT(600), values: { bt_batches_created_total: 600 } })).toBe(start);
  });
});

const frame = (i: number, p95: number | null = 1, pending: number | null = 3, values: Record<string, number | null> = {}) => ({ at: AT(i), stages: STAGES.map((s) => ({ stage: s.stage, p50: p95, p95 })), pending, values });
const counts = (view: ReturnType<typeof addStagePoint>, name: string) => view.series.find((s) => s.name === name)!;
const p95s = (view: ReturnType<typeof addStagePoint>, stage: number) => view.stages[stage]!.points.map((p) => p.p95);

describe("parseStagePoint", () => {
  it("REQ-BT-059 keeps the time, the stages, the pending and the counts, with nulls as sent", () => {
    const point = { at: AT(3), stages: [{ stage: "api", p50: 0.012, p95: 0.084 }, { stage: "store", p50: null, p95: null }], pending: null, values: { bt_batches_created_total: 70, bt_batches_closed_total: null } };
    expect(parseStagePoint({ type: "stagePoint", ...point })).toEqual(point);
  });

  it.each([[{ at: "nope", stages: [], pending: 1 }], [{ stages: [], pending: 1, values: {} }], [{ at: AT(1), pending: 1, values: {} }]])("REQ-BT-059 a frame without a time or stages is dropped: %j", (bad) => {
    expect(parseStagePoint(bad)).toBeNull();
  });

  it("REQ-BT-059 a value that is not a number reads unknown, never zero", () => {
    expect(parseStagePoint({ at: AT(1), stages: [{ stage: "api", p50: "1", p95: 2 }], pending: "3", values: { a: "4", b: 5 } })).toEqual({ at: AT(1), stages: [{ stage: "api", p50: null, p95: 2 }], pending: null, values: { a: null, b: 5 } });
  });

  it("REQ-BT-059 arrives from the monitor socket as a frame", () => {
    const point = frame(3);
    expect(parseMonitorFrame(JSON.stringify({ type: "stagePoint", ...point }))).toEqual({ type: "stagePoint", point });
    expect(parseMonitorFrame(JSON.stringify({ type: "stagePoint", at: "nope", stages: [] }))).toBeNull();
  });
});

describe("addStagePoint", () => {
  const start = () => toView(reply(), "10s");

  it("REQ-BT-059 appends one bucket to every stage and to pending, never summed", () => {
    const view = addStagePoint(start(), frame(3, 2, 9));
    expect(p95s(view, 2)).toEqual([0.2, 8, 0.4, 2]);
    expect(view.pending.at(-1)).toEqual({ at: AT(3), value: 9 });
    expect(stageView(view)!.buckets.at(-1)).toBe(AT(3));
  });

  it("REQ-BT-059 a frame with nulls stays null, and a stage it leaves out reads unknown", () => {
    const view = addStagePoint(start(), { at: AT(3), stages: [{ stage: "api", p50: null, p95: null }], pending: null, values: {} });
    expect(p95s(view, 0).at(-1)).toBeNull();
    expect(p95s(view, 3).at(-1)).toBeNull();
    expect(view.pending.at(-1)!.value).toBeNull();
  });

  it("REQ-BT-059 the counts land in the same bucket as the stages, and move the total and average", () => {
    const view = addStagePoint(start(), frame(3, 1, 3, { bt_batches_created_total: 90, bt_batches_closed_total: null }));
    const created = counts(view, "bt_batches_created_total");
    expect(created.points.at(-1)).toEqual({ at: view.stages[0]!.points.at(-1)!.at, value: 90 });
    expect(created.total).toBe(240);
    expect(created.averagePerMinute).toBe(360);
    expect(counts(view, "bt_batches_closed_total").points.at(-1)!.value).toBeNull();
  });

  it("REQ-BT-059 a count missing from the frame reads unknown, and stageView gives it per second", () => {
    const stages = stageView(addStagePoint(start(), frame(3, 1, 3, { bt_batches_created_total: 90 })))!;
    expect(stages.started.at(-1)!.value).toBe(9);
    expect(stages.closed.at(-1)!.value).toBeNull();
    expect(stages.latestStarted).toBe(9);
  });

  it("REQ-BT-059 a repeated frame never counts twice", () => {
    const once = addStagePoint(start(), frame(3, 1, 3, { bt_batches_created_total: 90 }));
    expect(addStagePoint(once, frame(3, 1, 3, { bt_batches_created_total: 90 }))).toBe(once);
  });

  it("REQ-BT-059 a bucket the counts already hold is never added again", () => {
    const view = start();
    expect(addBucket(view, Date.parse(AT(2)), { bt_batches_created_total: 90 })).toBe(view);
  });

  it("REQ-BT-059 buckets missed between frames stay as gaps in the counts too", () => {
    const view = addStagePoint(start(), frame(5, 1, 3, { bt_batches_created_total: 90 }));
    expect(counts(view, "bt_batches_created_total").points.map((p) => p.value)).toEqual([70, 80, null, null, null, 90]);
  });

  it("REQ-BT-059 a repeated or older frame changes nothing", () => {
    const view = start();
    expect(addStagePoint(view, frame(2))).toBe(view);
    expect(addStagePoint(view, frame(1))).toBe(view);
  });

  it("REQ-BT-059 buckets missed between frames stay as gaps", () => {
    const view = addStagePoint(start(), frame(5));
    expect(view.stages[0]!.points.map((p) => p.at)).toEqual([AT(0), AT(1), AT(2), AT(3), AT(4), AT(5)]);
    expect(p95s(view, 0).slice(3)).toEqual([null, null, 1]);
    expect(view.pending.slice(3).map((p) => p.value)).toEqual([null, null, 3]);
  });

  it("REQ-BT-059 a frame between bucket starts lands on its bucket", () => {
    const view = addStagePoint(start(), { ...frame(3, 1, 3, { bt_batches_created_total: 90 }), at: new Date(Date.parse(AT(3)) + 4_000).toISOString() });
    expect(Date.parse(view.stages[0]!.points.at(-1)!.at)).toBe(Date.parse(AT(3)));
    expect(counts(view, "bt_batches_created_total").points.at(-1)!.at).toBe(view.stages[0]!.points.at(-1)!.at);
  });

  it("REQ-BT-059 the chart never holds more than 120 buckets", () => {
    let view = start();
    for (let i = 3; i < 200; i += 1) view = addStagePoint(view, frame(i));
    expect(view.stages[0]!.points).toHaveLength(MAX_BUCKETS);
    expect(view.pending).toHaveLength(MAX_BUCKETS);
    expect(view.stages[0]!.points.at(-1)!.at).toBe(AT(199));
  });

  it("REQ-BT-059 a wider view is left as read, since a frame is not summed into a bucket", () => {
    const view = toView(reply({ bucket: "30s" }), "30s");
    expect(addStagePoint(view, frame(30))).toBe(view);
  });

  it("REQ-BT-059 a Monitor view with no stages gains none", () => {
    const view = toView(reply({ stages: [] }), "10s");
    expect(addStagePoint(view, frame(3))).toBe(view);
  });
});

describe("formatDuration", () => {
  it.each([
    [0, "0 ms"],
    [0.0004, "0.4 ms"],
    [0.005, "5.0 ms"],
    [0.012, "12 ms"],
    [0.084, "84 ms"],
    [0.9994, "999 ms"],
    [1, "1.0 s"],
    [2.4, "2.4 s"],
    [59.9, "59.9 s"],
    [60, ">= 60 s"],
    [100, "100.0 s"],
  ])("REQ-BT-059 %s seconds reads %s", (seconds, words) => {
    expect(formatDuration(seconds)).toBe(words);
  });
});
