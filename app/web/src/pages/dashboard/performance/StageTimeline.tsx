import type { ReactNode } from "react";
import { formatDateTime, formatTime } from "../../../logic/format";
import type { Bucket, MetricsView } from "../../../logic/monitor/metrics";
import { formatDuration, stageView, type SlowMark, type StageView } from "../../../logic/monitor/stages";
import { EmptyState, Grid, LineChart, Stack, Stat, Text } from "../../../ds";

const NO_DATA = "No data";
const perSecond = (value: number) => value.toFixed(1);
const rate = (value: number | null) => (value === null ? NO_DATA : perSecond(value));

function Slowest({ now, worst }: { now: SlowMark; worst: SlowMark }) {
  return (
    <Grid min="20rem">
      <Stat label="Slowest now" text={now.label} detail={`${formatDuration(now.seconds)} p95`} />
      <Stat label="Worst on screen" text={worst.label} detail={`${formatDuration(worst.seconds)} p95 at ${formatTime(worst.at)}`} />
    </Grid>
  );
}

function Figures({ stages }: { stages: StageView }) {
  return (
    <Grid min="9rem">
      <Stat label="Started per second" text={rate(stages.latestStarted)} />
      <Stat label="Closed per second" text={rate(stages.latestClosed)} />
      <Stat label="Pending peak" text={stages.latestPending === null ? NO_DATA : undefined} value={stages.latestPending} />
      {stages.p95.map((s) => (
        <Stat key={s.label} label={s.label} text={s.latest === null ? NO_DATA : formatDuration(s.latest)} detail="p95" />
      ))}
    </Grid>
  );
}

function Titled({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Stack gap={2}>
      <Text tone="secondary">{title}</Text>
      {children}
    </Stack>
  );
}

function Charts({ stages, sampled, formatAt }: { stages: StageView; sampled: boolean; formatAt: (at: string) => string }) {
  const { buckets } = stages;
  const series = [
    { label: "Started", points: stages.started },
    { label: "Closed", points: stages.closed },
  ];
  return (
    <>
      <Titled title="Batches per second">
        <LineChart caption="Batches per second" series={series} formatAt={formatAt} formatValue={perSecond} buckets={buckets} />
      </Titled>
      {sampled ? (
        <Titled title="Stage p95">
          <LineChart caption="Stage p95" series={stages.p95} formatAt={formatAt} formatValue={formatDuration} buckets={buckets} />
        </Titled>
      ) : null}
      <Titled title="Pending peak">
        <LineChart caption="Pending peak" series={[{ label: "Pending peak", points: stages.pending }]} formatAt={formatAt} buckets={buckets} />
      </Titled>
    </>
  );
}

function liveNote(bucket: Bucket, readAt: number | null): string {
  if (bucket === "10s") return "Batches, stages and pending arrive live, one bucket every 10 s.";
  // A failed read of a new bucket keeps the old view with no read time of its own.
  if (readAt === null) return "This view is from an earlier read. It does not move live.";
  // Neither frame fits 30 s: a metricPoint is a whole minute, and a stagePoint is never summed.
  if (bucket === "30s") return `This view is as read at ${formatTime(readAt)}. It does not move live.`;
  return `Stages and pending last read ${formatTime(readAt)}. They move live only at 10s buckets.`;
}

function Notes({ stages, bucket, readAt }: { stages: StageView; bucket: Bucket; readAt: number | null }) {
  const silent = stages.p95.filter((s) => s.latest === null);
  const gaps = stages.p95.some((s) => s.latest !== null && s.points.some((p) => p.value === null));
  return (
    <>
      {silent.length > 0 && silent.length < stages.p95.length ? (
        <Text size="xs" tone="secondary" as="p">
          No data in this view: {silent.map((s) => s.label).join(", ")}.
        </Text>
      ) : null}
      {gaps ? (
        <Text size="xs" tone="secondary" as="p">
          Gaps are unknown buckets, not zero.
        </Text>
      ) : null}
      <Text size="xs" tone="secondary" as="p">
        {liveNote(bucket, readAt)}
      </Text>
    </>
  );
}

/** Rate, stage timing and backlog over one time axis, so a wave shows as one stage slowing while the others hold. */
export function StageTimeline({ view, readAt }: { view: MetricsView; readAt: number | null }) {
  const stages = stageView(view);
  if (!stages) return <EmptyState title="Stage timing is not reported">This Monitor sends no stage timing, so the time per stage cannot be shown.</EmptyState>;
  const { now, worst } = stages;
  return (
    <Stack gap={3}>
      <Text tone="secondary">Where the time goes</Text>
      {now && worst ? <Slowest now={now} worst={worst} /> : <EmptyState title="No stage timing in this view">No batch went through a stage in these buckets.</EmptyState>}
      <Figures stages={stages} />
      <Charts stages={stages} sampled={now !== null} formatAt={view.bucket === "1d" ? formatDateTime : formatTime} />
      <Notes stages={stages} bucket={view.bucket} readAt={readAt} />
    </Stack>
  );
}
