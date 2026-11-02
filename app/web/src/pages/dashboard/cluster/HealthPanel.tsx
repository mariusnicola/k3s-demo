import { type MonitorReport } from "../../../api/monitorTypes";
import { type Feed } from "../../../live/useLiveSocket";
import { checksFailing } from "../../../logic/feed/hops";
import { formatDateTime } from "../../../logic/format";
import { Button, ConnectionStatus, DataTable, ErrorState, Panel, Skeleton, StatusBadge } from "../../../ds";

function HealthVerdict({ report, fresh }: { report: MonitorReport; fresh: boolean }) {
  const failing = checksFailing(report);
  return (
    fresh ? (
      report.checks.length === 0 ? (
        <StatusBadge tone="neutral" icon="clock">
          Checks have not run yet
        </StatusBadge>
      ) : failing.length === 0 ? (
        <StatusBadge tone="success" icon="check">
          All {report.checks.length} checks healthy
        </StatusBadge>
      ) : (
        <StatusBadge tone="danger" icon="blocked">
          {failing.length} of {report.checks.length} checks failing
        </StatusBadge>
      )
    ) : (
      <StatusBadge tone="neutral" icon="clock">
        Unknown. Showing the last report.
      </StatusBadge>
    )
  );
}

function HealthTable({ report, fresh }: { report: MonitorReport; fresh: boolean }) {
  return (
    <DataTable
      tiles="compact"
      caption="Health checks"
      columns={[
        { key: "name", header: "Check", render: (c) => c.name },
        {
          key: "state",
          header: "Status",
          render: (c) =>
            !fresh ? (
              <StatusBadge tone="neutral">{c.healthy ? "Was healthy" : "Was failing"}</StatusBadge>
            ) : c.healthy ? (
              <StatusBadge tone="success" icon="check">
                Healthy
              </StatusBadge>
            ) : (
              <StatusBadge tone="danger" icon="close">
                Failing
              </StatusBadge>
            ),
        },
        { key: "error", header: "Detail", render: (c) => c.error },
      ]}
      rows={report.checks}
      rowKey={(c) => c.name}
    />
  );
}

function HealthWaiting({ feed }: { feed: Feed }) {
  return (
    feed.state.status === "lost" ? (
      <ErrorState title="No report from the monitor" action={<Button onClick={feed.reconnect}>Reconnect now</Button>}>
        The monitor may be stopped. Cluster state is unknown until it answers.
      </ErrorState>
    ) : (
      <Skeleton rows={4} label="Waiting for the first monitor report" />
    )
  );
}

export function HealthPanel({ report, feed, now, fresh, stale }: { report: MonitorReport | null; feed: Feed; now: number; stale: boolean; fresh: boolean }) {
  const status = <ConnectionStatus label="Monitor" state={feed.state} now={now} stale={stale} onReconnect={feed.reconnect} />;
  if (!report) {
    return (
      <Panel title="Health" actions={status}>
        <HealthWaiting feed={feed} />
      </Panel>
    );
  }
  return (
    <Panel title="Health" description={`Checked ${formatDateTime(report.checkedAt)}`} actions={status}>
      <HealthVerdict report={report} fresh={fresh} />
      <HealthTable report={report} fresh={fresh} />
    </Panel>
  );
}
