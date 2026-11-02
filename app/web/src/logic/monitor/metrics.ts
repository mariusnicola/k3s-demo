export const BUCKETS = ["10s", "30s", "1m", "30m", "1h", "3h", "1d"] as const;
export type Bucket = (typeof BUCKETS)[number];
/** Short enough that a wave reads as a shape, not as one point. */
export const DEFAULT_BUCKET: Bucket = "10s";
/** Two hours at 1m. */
export const MAX_BUCKETS = 120;

const MINUTE_MS = 60_000;
const BUCKET_MS: Record<Bucket, number> = { "10s": 10_000, "30s": 30_000, "1m": MINUTE_MS, "30m": 30 * MINUTE_MS, "1h": 60 * MINUTE_MS, "3h": 180 * MINUTE_MS, "1d": 1440 * MINUTE_MS };
const STORE_KEY = "bt.metricsBucket";

/** One line per metric: the service sends `name`, the screen shows `label`, and `group` picks the chart. */
export const METRICS = [
  { name: "bt_events_created_total", label: "Events created", group: "Events" },
  { name: "bt_events_passed_total", label: "Events passed", group: "Events" },
  { name: "bt_batches_created_total", label: "Batches created", group: "Batches" },
  { name: "bt_dose1_total", label: "Dose 1", group: "Doses" },
  { name: "bt_dose2_total", label: "Dose 2", group: "Doses" },
  { name: "bt_inspections_passed_total", label: "Inspections passed", group: "Inspections" },
  { name: "bt_inspections_failed_total", label: "Inspections failed", group: "Inspections" },
  { name: "bt_batches_shipped_total", label: "Shipped", group: "Batches" },
  { name: "bt_batches_closed_total", label: "Closed", group: "Batches" },
  { name: "bt_batches_rejected_total", label: "Rejected", group: "Batches" },
] as const;

export const METRIC_GROUPS = [...new Set(METRICS.map((m) => m.group))];

export interface MetricValue {
  at: string;
  value: number | null;
}

export interface MetricSeries {
  name: string;
  total: number;
  averagePerMinute: number;
  points: MetricValue[];
}

export interface StagePoint {
  at: string;
  p50: number | null;
  p95: number | null;
}

export interface StageSeries {
  stage: string;
  points: StagePoint[];
}

export interface MetricsReply {
  bucket: string;
  available: boolean;
  error: string | null;
  historyStart: string | null;
  series: MetricSeries[];
  /** Absent from a Monitor older than stage timing. */
  stages?: StageSeries[] | undefined;
  pending?: MetricValue[] | undefined;
}

export interface MetricPoint {
  at: string;
  values: Record<string, number>;
}

export interface MetricsView {
  bucket: Bucket;
  historyStart: string | null;
  series: MetricSeries[];
  /** The socket never carries these, so they stay as the last read gave them. */
  stages: StageSeries[];
  pending: MetricValue[];
  /** Everything before this time is already counted, so a repeated point cannot count twice. */
  through: number;
}

export function readBucket(): Bucket {
  try {
    const stored = localStorage.getItem(STORE_KEY);
    return BUCKETS.find((bucket) => bucket === stored) ?? DEFAULT_BUCKET;
  } catch {
    return DEFAULT_BUCKET;
  }
}

export function storeBucket(bucket: Bucket): void {
  try {
    localStorage.setItem(STORE_KEY, bucket);
  } catch {
    /* A blocked store only costs the default on the next visit. */
  }
}

export function parseMetricPoint(value: Record<string, unknown>): MetricPoint | null {
  if (typeof value.at !== "string" || Number.isNaN(Date.parse(value.at)) || typeof value.values !== "object" || value.values === null) return null;
  return { at: value.at, values: Object.fromEntries(Object.entries(value.values).filter(([, v]) => typeof v === "number")) as Record<string, number> };
}

export const bucketSeconds = (bucket: Bucket): number => BUCKET_MS[bucket] / 1000;

export function toView(reply: MetricsReply, bucket: Bucket): MetricsView {
  const last = Math.max(0, ...reply.series.flatMap((s) => s.points.map((p) => Date.parse(p.at))));
  return { bucket, historyStart: reply.historyStart, series: reply.series, stages: reply.stages ?? [], pending: reply.pending ?? [], through: last + BUCKET_MS[bucket] };
}

function total(points: readonly MetricValue[]): number {
  return points.reduce((sum, p) => sum + (p.value ?? 0), 0);
}

function withPoint(series: MetricSeries, open: number, value: number | null, view: MetricsView, through: number): MetricSeries {
  const width = BUCKET_MS[view.bucket];
  const points = [...series.points];
  const last = points.at(-1);
  if (last && Date.parse(last.at) === open) {
    points[points.length - 1] = { at: last.at, value: value === null ? last.value : (last.value ?? 0) + value };
  } else {
    // Missed buckets stay as nulls, so the line breaks instead of joining across an outage.
    for (let at = last ? Date.parse(last.at) + width : open; at < open; at += width) points.push({ at: new Date(at).toISOString(), value: null });
    points.push({ at: new Date(open).toISOString(), value });
  }
  const kept = points.slice(-MAX_BUCKETS);
  const sum = total(kept);
  const first = Math.max(Date.parse(kept[0]!.at), view.historyStart ? Date.parse(view.historyStart) : 0);
  return { ...series, points: kept, total: sum, averagePerMinute: sum / ((through - first) / MINUTE_MS) };
}

const isCounter = (name: string) => METRICS.some((metric) => metric.name === name);

/** One closed bucket of the view's own width, counted by the Monitor, so nothing is added to an earlier bucket. */
export function addBucket(view: MetricsView, open: number, values: Record<string, number | null>): MetricsView {
  if (open < view.through) return view;
  const through = open + BUCKET_MS[view.bucket];
  return { ...view, through, series: view.series.map((s) => (isCounter(s.name) ? withPoint(s, open, values[s.name] ?? null, view, through) : s)) };
}

/** A point is the minute that just closed. A wider view adds it to its open bucket; a narrower one cannot split it, so it waits for the next read. Only counters can be added, so anything else keeps what its read gave it. */
export function addPoint(view: MetricsView, point: MetricPoint): MetricsView {
  const at = Date.parse(point.at);
  const width = BUCKET_MS[view.bucket];
  if (at < view.through || width < MINUTE_MS) return view;
  const open = Math.floor(at / width) * width;
  const through = at + MINUTE_MS;
  return { ...view, through, series: view.series.map((s) => (isCounter(s.name) ? withPoint(s, open, point.values[s.name] ?? null, view, through) : s)) };
}
