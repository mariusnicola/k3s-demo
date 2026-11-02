import type { Batch, Me } from "../../../api/types";
import { useSession } from "../../../auth/session";
import { useNow } from "../../../live/clock";
import { eventStatus, stepGate, type EventStatus } from "../../../logic/batch/batchRules";
import { formatDateTime } from "../../../logic/format";
import { countText, countToday, farmWords, oldestFirst } from "../../../logic/batch/roleDashboard";
import { AppLink, Grid, PageHeader, SplitLayout, Stack, Stat, type Column } from "../../../ds";
import { BatchList } from "./BatchList";
import { BatchesByStepPanel } from "./BatchesByStepPanel";
import { batchPath, farmColumn } from "./roleColumns";
import { FailedCounts, MissedReads, countOf, useKnownSummary, useList, type ListRead } from "./roleReads";

const EVENT_WORDS: Record<EventStatus, string> = { idle: "Ready", inflight: "Event in flight", delayed: "Delayed", blocked: "Blocked" };

function inspectorColumns(me: Me | null, now: number): Column<Batch>[] {
  return [
    { key: "product", header: "Batch", render: (b) => <AppLink to={batchPath(b)}>{`${stepGate(b, me, now) === null ? "Inspect" : "Open"} ${b.product}`}</AppLink> },
    farmColumn,
    { key: "status", header: "Status", render: (b) => (stepGate(b, me, now) === null ? "Ready" : EVENT_WORDS[eventStatus(b, now)]) },
    { key: "created", header: "Created", render: (b) => formatDateTime(b.createdAt) },
  ];
}

function passedToday(moved: ListRead[], now: number): string {
  return moved.some((l) => !l.rows) ? (moved.some((l) => l.list.error) ? "Unknown" : "Loading") : countText(moved.reduce((sum, l) => sum + countToday(l.rows!, "Inspected", now), 0), moved.some((l) => l.capped));
}

function InspectionStats({ total, loading, passedText, rejected, now }: { total: number | null; loading: string | undefined; passedText: string; rejected: ListRead; now: number }) {
  return (
    <Stack gap={3}>
      <Stat hero label="Waiting for inspection" value={total} text={loading} />
      <Grid min="9rem">
        <Stat label="Passed today" text={passedText} />
        <Stat label="Failed today" text={countOf(rejected, (rows) => countToday(rows, "Rejected", now))} />
      </Grid>
    </Stack>
  );
}

function InspectionQueue({ rows, read, columns, total }: { rows: Batch[] | undefined; read: ListRead; columns: Column<Batch>[]; total: number | null }) {
  return (
    <BatchList
      title="Waiting for inspection"
      rows={rows}
      read={read}
      columns={columns}
      empty="Nothing is waiting for inspection"
      note={rows && total !== null && total > rows.length ? `Showing the newest ${rows.length} of ${total}. The service lists newest first, so older batches are not here. Narrow by farm on Batches to reach them.` : null}
    />
  );
}

export function InspectorDashboard() {
  const { me } = useSession();
  const now = useNow();
  const waiting = useList("Dose2");
  const inspected = useList("Inspected");
  const shipped = useList("Shipped");
  const closed = useList("Closed");
  const rejected = useList("Rejected");
  const moved = [inspected, shipped, closed];
  const passedText = passedToday(moved, now);
  const { summary, loading } = useKnownSummary();
  const total = summary ? (summary.byState.Dose2 ?? 0) : null;
  const rows = waiting.rows ? oldestFirst(waiting.rows) : undefined;
  return (
    <>
      <PageHeader title="Dashboard" subtitle={`Inspection across ${farmWords(me)}.`} />
      <MissedReads reads={[waiting, ...moved, rejected]} />
      <FailedCounts reads={[...moved, rejected]} />
      <SplitLayout
        aside={<InspectionStats total={total} loading={loading} passedText={passedText} rejected={rejected} now={now} />}
      >
        <InspectionQueue rows={rows} read={waiting} columns={inspectorColumns(me, now)} total={total} />
      </SplitLayout>
      <BatchesByStepPanel />
    </>
  );
}
