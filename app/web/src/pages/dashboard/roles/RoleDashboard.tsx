import { BATCH_STATES, type Batch, type BatchState } from "../../../api/types";
import { useSession } from "../../../auth/session";
import { formatDateTime } from "../../../logic/format";
import { closedNewestFirst, farmWords, stepTime, type DashboardRole } from "../../../logic/batch/roleDashboard";
import { AppLink, Grid, PageHeader, Stat, type Column } from "../../../ds";
import { BatchList } from "./BatchList";
import { BatchesByStepPanel } from "./BatchesByStepPanel";
import { InspectorDashboard } from "./InspectorDashboard";
import { OperatorDashboard } from "./OperatorDashboard";
import { batchPath, farmColumn } from "./roleColumns";
import { MissedReads, useKnownSummary, useList } from "./roleReads";

const RECENT_ROWS = 10;

/** Every state but the two that end a batch. */
function openBatches(byState: Partial<Record<BatchState, number>>): number {
  return BATCH_STATES.filter((state) => state !== "Closed" && state !== "Rejected").reduce((sum, state) => sum + (byState[state] ?? 0), 0);
}

function ReaderStats() {
  const { summary, loading } = useKnownSummary();
  return (
    <Grid min="9rem">
      <Stat label="Open batches" value={summary ? openBatches(summary.byState) : null} text={loading} to="/batches" />
      <Stat label="Blocked" value={summary?.blocked ?? null} text={loading} tone="danger" to="/alerts" />
      <Stat label="Flagged" value={summary?.flagged ?? null} text={loading} tone="warning" to="/alerts" />
      <Stat label="Closed in total" value={summary ? (summary.byState.Closed ?? 0) : null} text={loading} />
    </Grid>
  );
}

function ReaderDashboard() {
  const { me } = useSession();
  const closed = useList("Closed");
  const columns: Column<Batch>[] = [
    { key: "product", header: "Product", render: (b) => <AppLink to={batchPath(b)}>{b.product}</AppLink> },
    farmColumn,
    { key: "closed", header: "Closed", render: (b) => formatDateTime(stepTime(b, "Closed") ?? b.createdAt) },
  ];
  return (
    <>
      <PageHeader title="Dashboard" subtitle={`Batches on ${farmWords(me)}. This view is read only.`} />
      <ReaderStats />
      <BatchesByStepPanel />
      <MissedReads reads={[closed]} />
      <BatchList title="Recently closed" rows={closed.rows ? closedNewestFirst(closed.rows).slice(0, RECENT_ROWS) : undefined} read={closed} columns={columns} empty="No closed batches yet" />
    </>
  );
}

export function RoleDashboard({ role }: { role: Exclude<DashboardRole, "admin"> }) {
  if (role === "inspector") return <InspectorDashboard />;
  return role === "operator" ? <OperatorDashboard /> : <ReaderDashboard />;
}
