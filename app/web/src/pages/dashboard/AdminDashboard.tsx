import { type MonitorReport } from "../../api/monitorTypes";
import { useNow } from "../../live/clock";
import { useHopFeed, useHopState } from "../../live/hopFeed";
import { useHopFlow } from "../../live/useHopFlow";
import { checksFailing } from "../../logic/feed/hops";
import { PageHeader, SplitLayout, Stack, Stat } from "../../ds";
import { ChangesPanel } from "./cluster/ChangesPanel";
import { ClusterPanels } from "./cluster/ClusterPanels";
import { HealthPanel } from "./cluster/HealthPanel";
import { FailurePanel } from "./controls/FailurePanel";
import { LinePanel } from "./controls/LinePanel";
import { NightlyPanel } from "./controls/NightlyPanel";
import { FlowPanel } from "./flow/FlowPanel";
import { RecentHops } from "./flow/RecentHops";
import { WorkersPanel } from "./flow/WorkersPanel";
import { PerformancePanel } from "./performance/PerformancePanel";
import { useMonitorReport } from "./useMonitorReport";

function AdminAside({ report, fresh, now }: { report: MonitorReport | null; fresh: boolean; now: number }) {
  return (
    <Stack gap={3}>
      <Stat hero label="Checks healthy" text={report && fresh && report.checks.length > 0 ? `${report.checks.length - checksFailing(report).length} of ${report.checks.length}` : "Unknown"} />
      <LinePanel now={now} />
      <NightlyPanel />
      <FailurePanel />
    </Stack>
  );
}

export function AdminDashboard() {
  const now = useNow();
  const flow = useHopFlow();
  const hopFeed = useHopFeed(flow.ingest);
  const { state } = useHopState();
  const { report, monitorFeed, fresh, stale } = useMonitorReport(now);

  return (
    <>
      <PageHeader title="Dashboard" subtitle="Live view of the running system, with controls to load it and break it." />
      <WorkersPanel report={report} fresh={fresh} lost={monitorFeed.state.status === "lost"} />
      <FlowPanel flow={flow} feed={hopFeed} now={now} report={report} fresh={fresh} summary={state.summary} pause={state.pause} />
      <SplitLayout aside={<AdminAside report={report} fresh={fresh} now={now} />}>
        <RecentHops flow={flow} feed={hopFeed} />
      </SplitLayout>
      <PerformancePanel feed={monitorFeed} now={now} stale={stale} />
      <HealthPanel report={report} feed={monitorFeed} now={now} fresh={fresh} stale={stale} />
      <ClusterPanels report={report} fresh={fresh} lost={monitorFeed.state.status === "lost"} />
      <ChangesPanel stale={stale} />
    </>
  );
}
