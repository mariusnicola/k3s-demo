import type { Batch } from "../../api/types";
import { type Feed } from "../../live/useLiveSocket";
import { formatTime, shownTime } from "../../logic/format";
import { Cluster, ConnectionStatus, StateBadge, StateRail, StatusBadge, Text } from "../../ds";

export function BatchStatusRow({ batch, feed, now, updatedAt, missed }: { batch: Batch; feed: Feed; now: number; updatedAt: number | null; missed: boolean }) {
  return (
    <Cluster gap={3}>
      <StateRail state={batch.state} />
      <StateBadge state={batch.state} />
      {batch.failNext && batch.pending?.status !== "dead" ? (
        <StatusBadge tone="warning" icon="warning">
          Next delivery set to fail
        </StatusBadge>
      ) : null}
      {batch.flags.map((flag) => (
        <StatusBadge key={`${flag.type}-${flag.at}`} tone="warning" icon="flag">
          {flag.type === "Dose2Overdue" ? "Dose 2 overdue" : flag.type}
        </StatusBadge>
      ))}
      <ConnectionStatus label="Live updates" state={feed.state} now={now} onReconnect={feed.reconnect} />
      <Text size="xs" tone="secondary">
        Updated {formatTime(shownTime(missed, updatedAt, feed.state.lastDataAt))}
      </Text>
    </Cluster>
  );
}
