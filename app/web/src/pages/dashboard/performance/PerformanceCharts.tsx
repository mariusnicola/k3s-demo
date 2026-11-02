import { formatDateTime, formatTime } from "../../../logic/format";
import { METRICS, METRIC_GROUPS, type MetricSeries, type MetricsView } from "../../../logic/monitor/metrics";
import { EmptyState, Grid, LineChart, Skeleton, Stack, Stat, Text } from "../../../ds";
import { ProblemState } from "../../shared";
import type { Resource } from "../../../live/useResource";
import { StageTimeline } from "./StageTimeline";

function hasGap(view: MetricsView, group: string): boolean {
  return METRICS.filter((m) => m.group === group).some((m) => view.series.find((s) => s.name === m.name)?.points.some((p) => p.value === null));
}

function startsLate(view: MetricsView): boolean {
  const first = view.series[0]?.points[0];
  return Boolean(view.historyStart && first && Date.parse(view.historyStart) > Date.parse(first.at));
}

function Tiles({ view }: { view: MetricsView }) {
  const find = (name: string): MetricSeries | undefined => view.series.find((s) => s.name === name);
  return (
    <Grid min="9rem">
      {METRICS.map((metric) => {
        const series = find(metric.name);
        // A sum of nothing but unknowns is not a count of zero.
        const known = series?.points.some((p) => p.value !== null) ? series : undefined;
        return <Stat key={metric.name} label={metric.label} value={known?.total ?? null} detail={known ? `${known.averagePerMinute.toFixed(1)} per minute` : undefined} />;
      })}
    </Grid>
  );
}

function Charts({ view }: { view: MetricsView }) {
  const formatAt = view.bucket === "1d" ? formatDateTime : formatTime;
  return (
    <Grid min="28rem">
      {METRIC_GROUPS.map((group) => {
        const series = METRICS.filter((m) => m.group === group).flatMap((m) => {
          const found = view.series.find((s) => s.name === m.name);
          return found ? [{ label: m.label, points: found.points }] : [];
        });
        return series.length === 0 ? null : (
          <Stack key={group} gap={2}>
            <Text tone="secondary">{group}</Text>
            <LineChart caption={group} series={series} formatAt={formatAt} />
            {hasGap(view, group) ? (
              <Text size="xs" tone="secondary" as="p">
                Gaps are unknown buckets, not zero.
              </Text>
            ) : null}
          </Stack>
        );
      })}
    </Grid>
  );
}

export function PerformanceBody({ read, view }: { read: Resource<unknown>; view: MetricsView | null }) {
  return (
    read.error && !view ? (
      <ProblemState error={read.error} onRetry={read.reload} />
    ) : !view || (read.loading && !read.data && read.error === null) ? (
      <Skeleton rows={4} label="Loading performance" />
    ) : view.series.length === 0 ? (
      <EmptyState title="No performance data yet">Numbers appear here once the app has run for a full bucket.</EmptyState>
    ) : (
      <>
        {startsLate(view) ? (
          <Text size="xs" tone="secondary" as="p">
            History starts {formatDateTime(view.historyStart)}. Earlier buckets are unknown, not zero.
          </Text>
        ) : null}
        <StageTimeline view={view} readAt={read.updatedAt} />
        <Tiles view={view} />
        <Charts view={view} />
      </>
    )
  );
}
