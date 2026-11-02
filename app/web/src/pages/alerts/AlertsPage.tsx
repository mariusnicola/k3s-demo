import { useNow } from "../../live/clock";
import { useHopState } from "../../live/hopFeed";
import { type Feed } from "../../live/useLiveSocket";
import { formatTime } from "../../logic/format";
import type { HopState } from "../../logic/feed/live";
import { ConnectionStatus, EmptyState, ErrorState, PageHeader, Panel, Skeleton, Text, useDocumentTitle } from "../../ds";
import { SHORT_ALERTS } from "../shared";
import { AlertsTable } from "./AlertsTable";

function AlertsBody({ state, feed }: { state: HopState; feed: Feed }) {
  const alerts = state.alerts;
  return (
    (!alerts || alerts.length === 0) && feed.state.status === "lost" ? (
      <ErrorState title="Alerts are unknown">Live updates are lost. Alerts appear here as soon as they return.</ErrorState>
    ) : state.unreadAlerts !== null ? (
      <EmptyState icon="clock" title="The live service has not read the store yet">
        Alerts appear here as soon as it has.
      </EmptyState>
    ) : !alerts ? (
      <Skeleton rows={4} label="Loading alerts" />
    ) : alerts.length === 0 && state.capped ? (
      <EmptyState title="No alerts to show">{SHORT_ALERTS}</EmptyState>
    ) : alerts.length === 0 ? (
      <EmptyState icon="check" title="No open alerts">
        Overdue doses appear here after the nightly run. Blocked batches appear as they happen.
      </EmptyState>
    ) : (
      <>
        <AlertsTable alerts={alerts} />
        {state.capped ? (
          <Text size="xs" tone="secondary" as="p">
            {SHORT_ALERTS}
          </Text>
        ) : null}
      </>
    )
  );
}

export function AlertsPage() {
  const now = useNow();
  useDocumentTitle("Alerts");
  const { state, feed } = useHopState();
  const alerts = state.alerts;
  const updated = feed.state.lastDataAt;

  return (
    <>
      <PageHeader title="Alerts" subtitle="Batches that need someone to act, on your farms." />
      <Panel
        title="Open alerts"
        description={alerts && updated ? `Updated ${formatTime(updated)}` : undefined}
        actions={<ConnectionStatus label="Live updates" state={feed.state} now={now} onReconnect={feed.reconnect} />}
      >
        <AlertsBody state={state} feed={feed} />
      </Panel>
    </>
  );
}
