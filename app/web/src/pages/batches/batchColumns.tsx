import type { Batch } from "../../api/types";
import { eventStatus, isFinal } from "../../logic/batch/batchRules";
import { formatDateTime, shortId } from "../../logic/format";
import { AppLink, Cluster, IdText, StateBadge, StateRail, StatusBadge, Text, type Column } from "../../ds";

function EventBadges({ batch, now }: { batch: Batch; now: number }) {
  const status = eventStatus(batch, now);
  // The Flagged filter and the server leave a final batch out, so the badge does too.
  const flagged = batch.flags.length > 0 && !isFinal(batch.state);
  return (
    <Cluster gap={1}>
      {status === "blocked" ? (
        <StatusBadge tone="danger" icon="blocked">
          Blocked
        </StatusBadge>
      ) : null}
      {status === "delayed" ? (
        <StatusBadge tone="warning" icon="clock">
          Delayed
        </StatusBadge>
      ) : null}
      {status === "inflight" ? (
        <StatusBadge tone="info" icon="clock">
          In flight
        </StatusBadge>
      ) : null}
      {flagged ? (
        <StatusBadge tone="warning" icon="flag">
          {batch.flags.length === 1 ? "Flagged" : `${batch.flags.length} flags`}
        </StatusBadge>
      ) : null}
      {status === "idle" && !flagged ? <Text tone="muted">None</Text> : null}
    </Cluster>
  );
}

/** Attention is read at the time the list was held, so a held row does not change under the pointer. */
export function batchColumns(attentionAt: number): Column<Batch>[] {
  return [
    {
      key: "product",
      header: "Product",
      render: (b) => <AppLink to={`/batches/${encodeURIComponent(b.farmId)}/${encodeURIComponent(b.id)}`}>{b.product}</AppLink>,
    },
    { key: "id", header: "Batch", render: (b) => <IdText value={b.id} short={shortId(b.id)} /> },
    { key: "farm", header: "Farm", render: (b) => b.farmId },
    {
      key: "state",
      header: "State",
      render: (b) => (
        <Cluster gap={2}>
          <StateRail state={b.state} />
          <StateBadge state={b.state} />
        </Cluster>
      ),
    },
    { key: "attention", header: "Attention", render: (b) => <EventBadges batch={b} now={attentionAt} /> },
    { key: "created", header: "Created", render: (b) => formatDateTime(b.createdAt) },
  ];
}
