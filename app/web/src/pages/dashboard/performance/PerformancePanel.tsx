import { useEffect, useRef, useState } from "react";
import { client } from "../../../api/client";
import { useMetricPoints } from "../../../live/monitorFeed";
import { type Feed } from "../../../live/useLiveSocket";
import { useResource } from "../../../live/useResource";
import { formatTime } from "../../../logic/format";
import type { PerformanceFrame } from "../../../logic/feed/hops";
import { BUCKETS, addPoint, readBucket, storeBucket, toView, type Bucket, type MetricsView } from "../../../logic/monitor/metrics";
import { addStagePoint } from "../../../logic/monitor/stages";
import { Banner, ConnectionStatus, Panel, SegmentedControl, Text } from "../../../ds";
import { ProblemBanner } from "../../shared";
import { PerformanceBody } from "./PerformanceCharts";

const apply = (view: MetricsView, frame: PerformanceFrame): MetricsView => (frame.type === "stagePoint" ? addStagePoint(view, frame.point) : addPoint(view, frame.point));

function usePerformance() {
  const [bucket, setBucket] = useState<Bucket>(readBucket);
  const read = useResource((signal) => client.metricsSeries(bucket, signal).then((r) => r.data), [bucket]);
  const [view, setView] = useState<MetricsView | null>(null);
  // Points that land during the read wait here, or its answer would drop them.
  const early = useRef<PerformanceFrame[]>([]);

  useMetricPoints((frame) => {
    if (read.data) setView((current) => current && apply(current, frame));
    else early.current.push(frame);
  });

  useEffect(() => {
    if (!read.data) return;
    setView(early.current.reduce(apply, toView(read.data, bucket)));
    early.current = [];
    // The bucket changes with the read, so its answer is the only thing that moves this.
  }, [read.data]);

  const choose = (next: Bucket) => {
    storeBucket(next);
    setBucket(next);
  };
  return { bucket, read, view, choose };
}

export function PerformancePanel({ feed, now, stale }: { feed: Feed; now: number; stale: boolean }) {
  const { bucket, read, view, choose } = usePerformance();
  const lost = feed.state.status === "lost";

  return (
    <Panel title="Performance" description="Totals and average per minute over the buckets shown." actions={<ConnectionStatus label="Performance updates" state={feed.state} now={now} stale={stale} onReconnect={feed.reconnect} />}>
      <SegmentedControl legend="Bucket" options={BUCKETS.map((b) => ({ value: b, label: b }))} value={bucket} onChange={choose} />
      {read.error && view ? (
        <>
          <ProblemBanner error={read.error} onRetry={read.reload} />
          <Text size="xs" tone="secondary" as="p">
            Showing the last series read, at {view.bucket} buckets.
          </Text>
        </>
      ) : null}
      {lost && view ? (
        <Banner floating="panel" tone="warning" title="Live updates are lost" live="off">
          Buckets after {formatTime(feed.state.lastDataAt)} read as unknown.
        </Banner>
      ) : null}
      <PerformanceBody read={read} view={view} />
    </Panel>
  );
}
