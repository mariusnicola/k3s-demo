import { client } from "../../../api/client";
import { ApiError } from "../../../api/problem";
import type { IngressLogs, IngressRow, LineLogs, LogEntry, LogFilters, ServiceLogs } from "../../../api/types";
import { useResource, type Resource } from "../../../live/useResource";
import { formatDateTime, formatTime } from "../../../logic/format";
import { ingressReading, narrowsRead } from "../../../logic/monitor/ingressReading";
import { readsServiceLog } from "../../../logic/monitor/logs";
import { DataTable, Panel, Skeleton, Stack, Text, type Column } from "../../../ds";
import { ProblemBanner } from "../../shared";

const COLUMNS: Column<IngressRow>[] = [
  { key: "time", header: "Time", render: (row) => formatTime(row.at) },
  { key: "status", header: "Status", render: (row) => String(row.status) },
  { key: "upstream", header: "Upstream", render: (row) => row.upstreamStatus ?? <Text tone="muted">Not recorded</Text> },
  { key: "path", header: "Path", render: (row) => <Text mono>{row.path}</Text> },
];

/** A 503 here is the monitor saying it is not following the log, which the page reads out as unknown. */
function useIngressLog(correlationId: string | null) {
  return useResource(
    (signal): Promise<IngressLogs | null> => {
      if (!correlationId) return Promise.resolve(null);
      return client.ingressLogs(correlationId, signal).then(
        (reply) => reply.data,
        (error: unknown) => {
          if (error instanceof ApiError && error.status === 503) return { correlationId, available: false, since: null, rows: [] };
          throw error;
        },
      );
    },
    [correlationId],
  );
}

interface ReportTrailProps {
  filters: LogFilters;
  report: LogEntry | null;
  correlationId: string | null;
  services: Resource<ServiceLogs>;
  logs: Resource<LineLogs>;
}

/** Entries that left memory or passed the limit may be the ones that matter, so such a read proves nothing by what it lacks. */
function holdsAll(read: { evicted: number; droppedByLimit: number } | null): boolean {
  return read === null || (read.evicted === 0 && read.droppedByLimit === 0);
}

function readingOf({ filters, report, correlationId, services, logs }: ReportTrailProps, ingress: Resource<IngressLogs | null>) {
  const entries = services.data?.entries ?? [];
  const lineRows = logs.data?.entries.length ?? 0;
  const narrowed = narrowsRead(filters, report);
  return ingressReading({
    reportId: filters.reportId || null,
    report,
    correlationId,
    serviceSkipped: !readsServiceLog(filters),
    serviceFailed: Boolean(services.error),
    narrowed,
    reportsEvicted: (services.data?.browserEvicted ?? 0) > 0,
    pending: services.loading || logs.loading || ingress.loading,
    ingress: ingress.data ?? null,
    ingressFailed: Boolean(ingress.error),
    reachedService: lineRows > 0 || entries.some((entry) => entry.service !== "browser"),
    everyLogRead: !narrowed && !services.error && !logs.error && holdsAll(services.data ?? null) && holdsAll(logs.data ?? null),
  });
}

/** Beside the service and Line rows, because it answers the one thing they cannot: whether the request got here at all. */
export function ReportTrail(props: ReportTrailProps) {
  const ingress = useIngressLog(props.correlationId);
  const reading = readingOf(props, ingress);
  if (!props.filters.reportId && !props.filters.correlationId) return null;
  const { since, rows = [] } = ingress.data ?? {};
  return (
    <Panel title="Ingress" description={since ? `Holds from ${formatDateTime(since)}` : undefined}>
      <Stack gap={2}>
        {ingress.error ? <ProblemBanner error={ingress.error} onRetry={ingress.reload} /> : null}
        {reading ? <Text as="p">{reading}</Text> : <Skeleton rows={1} label="Reading the ingress log" />}
        {rows.length > 0 ? <DataTable caption="Ingress rows" columns={COLUMNS} rows={rows} rowKey={(row) => `${row.at}-${row.status}-${row.upstreamStatus}-${row.path}`} /> : null}
      </Stack>
    </Panel>
  );
}
