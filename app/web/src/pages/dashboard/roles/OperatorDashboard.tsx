import type { Batch, Summary } from "../../../api/types";
import { useSession } from "../../../auth/session";
import { useNow } from "../../../live/clock";
import { STATE_LABELS, nextStep } from "../../../logic/batch/batchRules";
import { formatCount } from "../../../logic/format";
import { countToday, farmWords, waitingForOperator } from "../../../logic/batch/roleDashboard";
import { AppLink, Grid, PageHeader, SplitLayout, Stack, Stat, type Column } from "../../../ds";
import { BatchList } from "./BatchList";
import { BatchesByStepPanel } from "./BatchesByStepPanel";
import { batchPath, farmColumn, stateColumn } from "./roleColumns";
import { ShortCounts } from "../../shared";
import { FailedCounts, MissedReads, countOf, useKnownSummary, useList, type ListRead } from "./roleReads";

const TABLE_ROWS = 20;

function nextLabel(batch: Batch): string {
  const next = nextStep(batch.state);
  return next.kind === "advance" ? `Move to ${STATE_LABELS[next.to]}` : "";
}

const COLUMNS: Column<Batch>[] = [
  { key: "product", header: "Product", render: (b) => <AppLink to={batchPath(b)}>{b.product}</AppLink> },
  farmColumn,
  stateColumn,
  { key: "next", header: "Next step", render: nextLabel },
];

function OperatorStats({ all, closed, waiting, summary, loading, now }: { all: ListRead; closed: ListRead; waiting: Batch[] | undefined; summary: Summary | null; loading: string | undefined; now: number }) {
  return (
    <Stack gap={3}>
      <Stat hero label="Waiting for your next step" text={countOf(all, () => waiting?.length ?? 0)} />
      <Grid min="9rem">
        <Stat label="Blocked" value={summary?.blocked ?? null} text={loading} tone="danger" to="/alerts" />
        <Stat label="Flagged" value={summary?.flagged ?? null} text={loading} tone="warning" to="/alerts" />
        <Stat label="Event in flight" value={summary?.pending ?? null} text={loading} to="/batches?attention=inflight" />
        <Stat label="Rejected" value={summary ? (summary.byState.Rejected ?? 0) : null} text={loading} to="/batches?state=Rejected" />
        <Stat label="Closed today" text={countOf(closed, (rows) => countToday(rows, "Closed", now))} />
      </Grid>
      {summary ? <ShortCounts /> : null}
    </Stack>
  );
}

function WaitingList({ all, waiting, held }: { all: ListRead; waiting: Batch[] | undefined; held: number }) {
  return (
    <BatchList
      title="Waiting for your next step"
      rows={waiting?.slice(0, TABLE_ROWS)}
      read={all}
      columns={COLUMNS}
      empty="Nothing is waiting for you"
      doubtful={held > 0}
      note={[waiting && waiting.length > TABLE_ROWS ? `Showing the newest ${TABLE_ROWS} of ${all.capped ? "at least " : ""}${waiting.length}.` : null, held > 0 ? `${formatCount(held)} ${held === 1 ? "batch has" : "batches have"} an event in flight, and may join this list when it lands.` : null].filter(Boolean).join(" ") || null}
    />
  );
}

export function OperatorDashboard() {
  const { me } = useSession();
  const now = useNow();
  const all = useList();
  const closed = useList("Closed");
  const waiting = all.rows ? waitingForOperator(all.rows, me, now) : undefined;
  const { summary, loading } = useKnownSummary();
  const held = summary?.pending ?? 0;
  return (
    <>
      <PageHeader title="Dashboard" subtitle={`Your work on ${farmWords(me)}.`} />
      <MissedReads reads={[all, closed]} />
      <FailedCounts reads={[closed]} />
      <SplitLayout
        aside={<OperatorStats all={all} closed={closed} waiting={waiting} summary={summary} loading={loading} now={now} />}
      >
        <WaitingList all={all} waiting={waiting} held={held} />
      </SplitLayout>
      <BatchesByStepPanel />
    </>
  );
}
