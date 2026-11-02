import type { Summary } from "../../api/types";
import { type Feed } from "../../live/useLiveSocket";
import { formatTime } from "../../logic/format";
import { Grid, LiveNote, Skeleton, Stat, Text } from "../../ds";
import { ShortCounts } from "../shared";

export function BatchTotals({ summary, feed }: { summary: Summary | null; feed: Feed }) {
  const open = (summary ? Object.entries(summary.byState).filter(([s]) => s !== "Closed" && s !== "Rejected") : []).reduce((sum, [, n]) => sum + (n ?? 0), 0);

  return (
    <>
      {summary ? (
        <Grid min="9rem">
          <Stat label="Open" value={open} />
          <Stat label="Closed" value={summary.byState.Closed ?? 0} />
          <Stat label="Rejected" value={summary.byState.Rejected ?? 0} />
          <Stat label="Event in flight" value={summary.pending} to="/batches?attention=inflight" />
          <Stat label="Blocked" value={summary.blocked} tone="danger" to="/alerts" />
          <Stat label="Flagged" value={summary.flagged} tone="warning" to="/alerts" />
        </Grid>
      ) : feed.state.status === "lost" ? (
        <Text tone="secondary" as="p">
          Totals are unknown until the live stream returns.
        </Text>
      ) : (
        <Skeleton rows={1} label="Loading summary" />
      )}
      {summary ? <ShortCounts /> : null}
      <LiveNote>{summary && feed.state.status === "lost" ? `Totals are from ${formatTime(feed.state.lastDataAt)}.` : null}</LiveNote>
    </>
  );
}
