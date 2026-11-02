import { useNow } from "../../../live/clock";
import { useHopState } from "../../../live/hopFeed";
import { formatDateTime } from "../../../logic/format";
import { stateCounts } from "../../../logic/batch/roleDashboard";
import { ConnectionStatus, EmptyState, FlowMap, LiveNote, Panel, Skeleton, useMediaQuery } from "../../../ds";
import { useKnownSummary } from "./roleReads";

function StepTotals({ waiting, summary, lost, compact, lastDataAt }: { waiting: boolean; summary: ReturnType<typeof useKnownSummary>["summary"]; lost: boolean; compact: boolean; lastDataAt: number | null }) {
  return (
    waiting ? (
      <EmptyState icon="clock" title="The live service has not read the store yet">
        Totals appear here as soon as it has.
      </EmptyState>
    ) : summary ? (
      <>
        <FlowMap counts={stateCounts(summary)} compact={compact} />
        <LiveNote>{lost ? `Totals are from ${formatDateTime(lastDataAt)}.` : null}</LiveNote>
      </>
    ) : lost ? (
      <EmptyState icon="clock" title="Totals unknown">
        Live updates are lost, and no totals arrived before that.
      </EmptyState>
    ) : (
      <Skeleton rows={4} label="Loading batches by step" />
    )
  );
}

export function BatchesByStepPanel() {
  const { state: live, feed } = useHopState();
  const now = useNow();
  const { summary } = useKnownSummary();
  const lost = feed.state.status === "lost";
  const compact = useMediaQuery("(max-width: 720px)");
  return (
    <Panel title="Batches by step" ground actions={<ConnectionStatus label="Live updates" state={feed.state} now={now} onReconnect={feed.reconnect} />}>
      <StepTotals waiting={live.unreadAlerts !== null && !lost} summary={summary} lost={lost} compact={compact} lastDataAt={feed.state.lastDataAt} />
    </Panel>
  );
}
