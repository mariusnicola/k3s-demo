import { HOP_NAMES, type Summary, type WorkerPause } from "../../../api/types";
import { type MonitorReport } from "../../../api/monitorTypes";
import type { FlowSnapshot } from "../../../live/hopFlowRefs";
import type { HopFlow } from "../../../live/useHopFlow";
import type { WaitView } from "../../../logic/flow/queueWait";
import { type Feed } from "../../../live/useLiveSocket";
import { counted, formatCount } from "../../../logic/format";
import { nodeHealth, padWords } from "../../../logic/feed/hops";
import { ArchitectureDiagram, ConnectionStatus, Panel, useMediaQuery } from "../../../ds";
import { FlowControls } from "./FlowControls";
import { FlowCounts, FlowNotices, MapFacts } from "./FlowNotes";

function waitWords({ count, oldestMs }: WaitView): string | null {
  return oldestMs === null ? null : `${formatCount(count)} waiting, oldest seen ${Math.round(oldestMs / 1000)} s`;
}

function mapWords({ failures, total, moving }: FlowSnapshot): string {
  const failed = HOP_NAMES.reduce((sum, name) => sum + failures[name], 0);
  return `Flow from API to compliance record. ${counted(total, "hop")} seen, ${formatCount(failed)} failed, ${counted(moving, "dot")} on screen.`;
}

interface FlowPanelProps {
  flow: HopFlow;
  feed: Feed;
  now: number;
  report: MonitorReport | null;
  fresh: boolean;
  summary: Summary | null;
  pause: WorkerPause | null;
}

function FlowDiagram({ flow, report, fresh, lost }: { flow: HopFlow; report: MonitorReport | null; fresh: boolean; lost: boolean }) {
  const compact = useMediaQuery("(max-width: 720px)");
  const { snapshot } = flow;
  return (
    <ArchitectureDiagram
      getFrame={flow.getFrame}
      counts={flow.realtime ? snapshot.counts : null}
      failures={snapshot.failures}
      podCounts={snapshot.podCounts}
      sender={report?.dispatcherLeader ?? snapshot.sender}
      waiting={flow.realtime ? waitWords(snapshot.waiting) : null}
      health={nodeHealth(report, fresh)}
      compact={compact}
      summary={mapWords(snapshot)}
      lost={lost}
      pods={report?.pods ?? null}
      store={report?.store ?? null}
      leader={Boolean(report?.dispatcherLeader)}
      padWords={padWords(report, fresh)}
      stale={!fresh}
    />
  );
}

export function FlowPanel({ flow, feed, now, report, fresh, summary: totals, pause }: FlowPanelProps) {
  const lost = feed.state.status === "lost";
  return (
    <Panel
      ground
      title="Architecture"
      description="Each dot is one batch step, following real hops through the services. Each small block is one pod, on its real node. Lines are the real connections; only dots move."
      actions={<ConnectionStatus label="Hop stream" state={feed.state} now={now} onReconnect={feed.reconnect} />}
    >
      <FlowControls flow={flow} />
      <FlowNotices flow={flow} lost={lost} />
      <MapFacts blocked={totals?.blocked ?? null} pause={pause} />
      <FlowDiagram flow={flow} report={report} fresh={fresh} lost={lost} />
      <FlowCounts flow={flow} />
    </Panel>
  );
}
