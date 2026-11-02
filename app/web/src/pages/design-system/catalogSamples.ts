import type { Batch, HopName, StationName, Step } from "../../api/types";
import type { PodReport } from "../../api/monitorTypes";
import type { SocketState } from "../../live/socket";
import type { StationHealth } from "../../logic/feed/hops";
import type { DotFrame } from "../../logic/flow/dots";
import type { ChartSeries, Tone } from "../../ds";

export const LONG_TEXT = "Bovine respiratory vaccine, multi-dose 500 ml vial, cold chain lot with a very long descriptive name for the record";
export const TONES: Tone[] = ["neutral", "info", "success", "warning", "danger"];
const HOPS: HopName[] = ["api", "dispatcher", "bus", "deliver", "worker", "record", "settle"];
const STATIONS: StationName[] = ["api", "cosmos", "dispatcher", "bus", "worker"];
export const counts = Object.fromEntries(HOPS.map((h, i) => [h, 120 - i])) as Record<HopName, number>;
export const noFailures = Object.fromEntries(HOPS.map((h) => [h, 0])) as Record<HopName, number>;
export const unknownHealth = Object.fromEntries(STATIONS.map((h) => [h, { state: "unknown", text: "No report yet" }])) as Record<StationName, StationHealth>;
export const liveFrame = (): DotFrame[] => [
  { traceId: "t1", from: "cosmos", to: "dispatcher", progress: 0.5, ok: true },
  { traceId: "t2", from: "dispatcher", to: "worker", progress: 0.7, ok: false },
];
export const noFrame = (): DotFrame[] => [];

export const steps: Step[] = [
  { state: "Created", at: "2026-10-30T09:00:00Z", by: "olivia", traceId: "t", recordId: "rec-1", hash: "aa11", previousHash: "" },
  { state: "Dose1", at: "2026-10-30T10:00:00Z", by: "olivia", traceId: "t", recordId: "", hash: "", previousHash: "" },
];

export const socket = (status: SocketState["status"], lastDataAt: number | null): SocketState => ({ status, lastDataAt });

export const sampleBatch = (at: number, status: "sent" | "dead"): Batch => ({
  id: "b-1",
  version: 1,
  farmId: "farm-a",
  product: "Bovine vaccine",
  state: "Dose1",
  createdAt: new Date(at).toISOString(),
  reason: null,
  failNext: false,
  flags: [],
  steps: [{ ...steps[1]!, at: new Date(at).toISOString() }],
  pending: { eventId: "b-1:Dose1", traceId: "t", status, sentAt: null, error: status === "dead" ? "Refused by the Worker." : null },
});

export const noop = () => undefined;

function samplePod(name: string, node: string, more: Partial<PodReport> = {}): PodReport {
  return { namespace: "batch-tracker", name, node, phase: "Running", ready: true, cpuLimitMilli: 200, memoryLimitMi: 192, cpuUsageMilli: 40, memoryUsageMi: 90, ...more };
}

export const samplePods: PodReport[] = [
  samplePod("api-5d9-a1b2c", "k3s-node1"),
  samplePod("api-5d9-d3e4f", "k3s-node1", { ready: false }),
  samplePod("dispatcher-5b7-g5h6j", "k3s-node1"),
  samplePod("worker-7c4-k7m8n", "k3s-node1"),
  samplePod("worker-7c4-p9q1r", "k3s-node1", { ready: true, terminating: true }),
  samplePod("live-6f8-s2t3u", "k3s-node1"),
  samplePod("web-6f8-v4w5x", "k3s-node1", { ready: undefined }),
  samplePod("worker-node2-6f8-y6z7a", "k3s-node2"),
  samplePod("worker-node2-6f8-b8c9d", "k3s-node2", { phase: "Pending", ready: false }),
  samplePod("worker-node2-6f8-e1f2g", "k3s-node2", { phase: "Failed", ready: false, cpuUsageMilli: null, memoryUsageMi: null }),
  samplePod("dispatcher-node2-5b7-h3j4k", "k3s-node2", { phase: "Unknown", ready: false }),
];

export function chartBucket(at: string): string {
  return at.slice(11, 16);
}

export function chartSeries(labels: string[], values: (number | null)[]): ChartSeries[] {
  return labels.map((label, line) => ({
    label,
    points: values.map((value, i) => ({ at: `2026-10-31T10:0${i}:00Z`, value: value === null ? null : value + line })),
  }));
}
