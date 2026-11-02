import type { BatchState, Hop } from "../../../api/types";
import { STATE_LABELS } from "../../../logic/batch/batchRules";
import { formatTime, shortId } from "../../../logic/format";
import { logsFor } from "../../../logic/monitor/logs";
import { AppLink, Button, Named, STATION_LABELS, StatusBadge, Text, type Column } from "../../../ds";

function HopResult({ ok }: { ok: boolean }) {
  return ok ? (
    <StatusBadge tone="success" icon="check">
      OK
    </StatusBadge>
  ) : (
    <StatusBadge tone="danger" icon="close">
      Failed
    </StatusBadge>
  );
}

export function recentHopColumns(onFollow: (traceId: string) => void): Column<Hop>[] {
  return [
    { key: "at", header: "Time", render: (h) => formatTime(h.at) },
    { key: "hop", header: "Service", render: (h) => STATION_LABELS[h.hop] },
    { key: "state", header: "State", render: (h) => STATE_LABELS[h.state as BatchState] ?? h.state },
    { key: "ok", header: "Result", render: (h) => <HopResult ok={h.ok} /> },
    { key: "correlation", header: "Correlation ID", render: (h) => (h.correlationId ? <AppLink to={logsFor(h.correlationId)} label={`Open ${h.correlationId} in Logs`}>{shortId(h.correlationId)}</AppLink> : <Text tone="muted">None</Text>) },
    { key: "batch", header: "Batch", render: (h) => <AppLink to={`/batches/${encodeURIComponent(h.farmId)}/${encodeURIComponent(h.batchId)}`}>{shortId(h.batchId)}</AppLink> },
    {
      key: "trace",
      header: "Trace",
      render: (h) => (
        <Button variant="ghost" onClick={() => onFollow(h.traceId)}>
          <Named name={`Follow trace ${h.traceId}`}>{shortId(h.traceId)}</Named>
        </Button>
      ),
    },
  ];
}
