import { type MonitorReport } from "../../../api/monitorTypes";
import { formatCount, formatDateTime } from "../../../logic/format";
import { workersView, type WorkerUnit, type WorkersView } from "../../../logic/feed/workers";
import type { PodState } from "../../../logic/terrain/pods";
import { EmptyState, KeyValues, Panel, Stack, UnitGrid, type IconName, type Unit } from "../../../ds";
import { MissingSection, monitorPlaceholder } from "../cluster/MissingSection";

const BUSY_LOOK: { busy: boolean | null; words: string; icon: IconName }[] = [
  { busy: true, words: "Working in the last minute", icon: "pulse" },
  { busy: false, words: "No work in the last minute", icon: "clock" },
  { busy: null, words: "Busy unknown", icon: "info" },
];

const LOOK: Record<PodState, Pick<Unit, "tone" | "icon">> = {
  ready: { tone: "success", icon: "check" },
  "not-reported": { tone: "neutral", icon: "info" },
  "not-ready": { tone: "warning", icon: "warning" },
  unknown: { tone: "warning", icon: "info" },
  pending: { tone: "warning", icon: "clock" },
  failed: { tone: "danger", icon: "close" },
  terminating: { tone: "neutral", icon: "stop" },
};

function standbyWords(standbys: string[] | null): string {
  if (!standbys) return "Unknown";
  return standbys.length === 0 ? "0" : `${formatCount(standbys.length)}: ${standbys.join(", ")}`;
}

/** A count that leaves Workers out says so, because it is not the whole. */
function countWords(count: number, { running, busyUnknown }: WorkersView): string {
  if (busyUnknown === 0) return formatCount(count);
  return busyUnknown === running ? "Unknown" : `At least ${formatCount(count)}`;
}

function figures(view: WorkersView, fresh: boolean) {
  const live = (text: string) => (fresh ? text : "Stale");
  return [
    { label: "Running", value: live(formatCount(view.running)) },
    { label: "Busy in the last minute", value: live(countWords(view.busy, view)) },
    { label: "Idle in the last minute", value: live(countWords(view.idle, view)) },
    { label: "Busy not known", value: live(formatCount(view.busyUnknown)) },
    { label: "Most possible", value: live(view.max === null ? "Unknown" : formatCount(view.max)) },
    { label: "Dispatcher leader", value: live(view.leader ?? "None reported") },
    { label: "Standbys", value: live(standbyWords(view.standbys)) },
  ];
}

function busyDetail(busy: boolean | null, fresh: boolean): NonNullable<Unit["detail"]> {
  const { words, icon } = BUSY_LOOK.find((look) => look.busy === busy)!;
  return fresh ? { words, icon } : { words: `Last known: ${words.toLowerCase()}`, icon: "clock" };
}

function unit(worker: WorkerUnit, fresh: boolean): Unit {
  const look = fresh ? LOOK[worker.state] : { tone: "neutral" as const, icon: "clock" as const };
  const words = fresh ? worker.words : `Was ${worker.words.toLowerCase()}`;
  return { id: worker.name, name: worker.label, note: `${worker.node}, ${words}`, detail: busyDetail(worker.busy, fresh), ...look };
}

function WorkersBody({ view, fresh }: { view: WorkersView; fresh: boolean }) {
  return (
    <Stack gap={3}>
      <KeyValues figures items={figures(view, fresh)} />
      {view.units.length > 0 ? (
        <UnitGrid caption="Workers" units={view.units.map((worker) => unit(worker, fresh))} />
      ) : (
        <EmptyState icon="info" title="No Worker pods in the report">
          The monitor read the cluster and found none.
        </EmptyState>
      )}
    </Stack>
  );
}

export function WorkersPanel({ report, fresh, lost }: { report: MonitorReport | null; fresh: boolean; lost: boolean }) {
  const view = report && workersView(report);
  const staleNote = report && !fresh ? `Last known values from ${formatDateTime(report.checkedAt)}.` : undefined;
  return (
    <Panel title="Workers" description={staleNote ?? "One block per Worker pod, on its real node. Busy means it passed an event in the last minute or has work in flight now."}>
      {!report ? monitorPlaceholder(lost) : view ? <WorkersBody view={view} fresh={fresh} /> : <MissingSection what="pods" />}
    </Panel>
  );
}
