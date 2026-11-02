import { MAX_BUCKETS, addBucket, bucketSeconds, type MetricValue, type MetricsView, type StagePoint, type StageSeries } from "./metrics";

/** The four stages a batch's time splits into, in the order the work runs. */
export const STAGES = [
  { stage: "api", label: "API call" },
  { stage: "store", label: "Database write" },
  { stage: "queue", label: "Waiting in the queue" },
  { stage: "worker", label: "Worker handling" },
];

export interface StageLine {
  label: string;
  points: MetricValue[];
  latest: number | null;
}

export interface SlowMark {
  label: string;
  seconds: number;
  at: string;
}

export interface StageView {
  buckets: string[];
  started: MetricValue[];
  closed: MetricValue[];
  pending: MetricValue[];
  p95: StageLine[];
  now: SlowMark | null;
  worst: SlowMark | null;
  latestStarted: number | null;
  latestClosed: number | null;
  latestPending: number | null;
}

/** One 10 s bucket off the socket. */
export interface StageFrame {
  at: string;
  stages: { stage: string; p50: number | null; p95: number | null }[];
  pending: number | null;
  values: Record<string, number | null>;
}

const FRAME_MS = bucketSeconds("10s") * 1000;
const numberOrNull = (value: unknown): number | null => (typeof value === "number" ? value : null);
const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null;

export function parseStagePoint(value: Record<string, unknown>): StageFrame | null {
  if (typeof value.at !== "string" || Number.isNaN(Date.parse(value.at)) || !Array.isArray(value.stages) || !isRecord(value.values)) return null;
  const stages = value.stages.filter(isRecord).flatMap((s) => (typeof s.stage === "string" ? [{ stage: s.stage, p50: numberOrNull(s.p50), p95: numberOrNull(s.p95) }] : []));
  const values = Object.fromEntries(Object.entries(value.values).map(([name, count]) => [name, numberOrNull(count)]));
  return { at: value.at, stages, pending: numberOrNull(value.pending), values };
}

function appended(series: StageSeries, times: string[], frame: StageFrame): StageSeries {
  const sent = frame.stages.find((s) => s.stage === series.stage);
  const missed: StagePoint[] = times.slice(0, -1).map((at) => ({ at, p50: null, p95: null }));
  const point = { at: times.at(-1)!, p50: sent?.p50 ?? null, p95: sent?.p95 ?? null };
  return { ...series, points: [...series.points, ...missed, point].slice(-MAX_BUCKETS) };
}

/** A frame is one bucket of a 10 s view. Percentiles cannot be added, so a wider view keeps what its read gave it. Buckets missed between frames stay as gaps. */
export function addStagePoint(view: MetricsView, frame: StageFrame): MetricsView {
  const last = view.stages[0]?.points.at(-1);
  if (view.bucket !== "10s" || !last) return view;
  const open = Math.floor(Date.parse(frame.at) / FRAME_MS) * FRAME_MS;
  const from = Date.parse(last.at) + FRAME_MS;
  if (open < from) return view;
  const times = Array.from({ length: (open - from) / FRAME_MS + 1 }, (_, i) => new Date(from + i * FRAME_MS).toISOString());
  const pending = [...view.pending, ...times.slice(0, -1).map((at) => ({ at, value: null })), { at: times.at(-1)!, value: frame.pending }].slice(-MAX_BUCKETS);
  return { ...addBucket(view, open, frame.values), stages: view.stages.map((s) => appended(s, times, frame)), pending };
}

const latest = (points: readonly MetricValue[]): number | null => points.filter((p) => p.value !== null).at(-1)?.value ?? null;

function highest(marks: SlowMark[]): SlowMark | null {
  return marks.reduce<SlowMark | null>((best, mark) => (!best || mark.seconds > best.seconds ? mark : best), null);
}

function perSecond(view: MetricsView, name: string): MetricValue[] {
  const seconds = bucketSeconds(view.bucket);
  const series = view.series.find((s) => s.name === name);
  return (series?.points ?? []).map(({ at, value }) => ({ at, value: value === null ? null : value / seconds }));
}

function line(view: MetricsView, stage: string, label: string): StageLine {
  const points = (view.stages.find((s) => s.stage === stage)?.points ?? []).map(({ at, p95 }) => ({ at, value: p95 }));
  return { label, points, latest: latest(points) };
}

/** Null when the Monitor reported no stages at all, which is not the same as stages with no samples. */
export function stageView(view: MetricsView): StageView | null {
  if (view.stages.length === 0) return null;
  const p95 = STAGES.map((s) => line(view, s.stage, s.label));
  const marks = p95.flatMap((s) => s.points.flatMap(({ at, value }) => (value === null ? [] : [{ label: s.label, seconds: value, at }])));
  const lastAt = Math.max(...marks.map((m) => Date.parse(m.at)));
  const started = perSecond(view, "bt_batches_created_total");
  const closed = perSecond(view, "bt_batches_closed_total");
  return {
    buckets: view.stages[0]!.points.map((p) => p.at),
    started,
    closed,
    pending: view.pending,
    p95,
    now: highest(marks.filter((m) => Date.parse(m.at) === lastAt)),
    worst: highest(marks),
    latestStarted: latest(started),
    latestClosed: latest(closed),
    latestPending: latest(view.pending),
  };
}

/** Seconds as the unit a person reads them in. The timing histogram tops out at 60, so that reading means 60 or more. */
export function formatDuration(seconds: number): string {
  if (seconds === 60) return ">= 60 s";
  if (seconds >= 0.9995) return `${seconds.toFixed(1)} s`;
  const ms = seconds * 1000;
  return ms === 0 ? "0 ms" : `${ms.toFixed(ms < 10 ? 1 : 0)} ms`;
}
