import { Navigate } from "react-router-dom";
import type { LogFilters } from "../../api/types";
import { useSession } from "../../auth/session";
import { useNow } from "../../live/clock";
import { can } from "../../logic/batch/batchRules";
import { formatTime, shownTime } from "../../logic/format";
import { listGaps, type ServiceRead } from "../../logic/monitor/listGaps";
import { counterNote, findReport, lineFiltersFor, readsLine, readsServiceLog, reasonCounts, withServiceEntries } from "../../logic/monitor/logs";
import { ConnectionStatus, PageHeader, Panel, useDocumentTitle } from "../../ds";
import { EntriesList } from "./LogEntries";
import { EntriesNotices } from "./EntriesNotices";
import { LogFilterFields } from "./LogFilterFields";
import { ReasonCountsPanel } from "./ReasonCountsPanel";
import { ReportTrail } from "./report/ReportTrail";
import { ServiceNotices } from "./ServiceNotices";
import { useLogFilters } from "./useLogFilters";
import { useLogFrames } from "./useLogFrames";
import { useServiceLogs } from "./useServiceLogs";

/** A press after an outage reads both logs: a read of the Line alone would leave service rows older than the Line's. */
function useLogRows(filters: LogFilters, services: ReturnType<typeof useServiceLogs>, frames: ReturnType<typeof useLogFrames>) {
  const { held, reply, lostRead, logs, feed, skipped, missed } = frames;
  const rows = held?.rows ?? withServiceEntries(reply?.entries ?? [], services.data?.entries ?? []);
  const readAgain = () => {
    void services.reload();
    return lostRead.readAgain();
  };
  // The latest read decides: rows from an older one do not make this one a success.
  const read: ServiceRead = !readsServiceLog(filters) ? "read" : services.error ? "failed" : services.data ? "read" : "pending";
  const { note, counts } = lineSummary(filters, frames);
  const gaps = listGaps({ lineFailed: Boolean(logs.error), line: reply, service: services.data ?? null, serviceRead: read, noLogRead: !readsServiceLog(filters) && !readsLine(filters), lost: feed.state.status === "lost", missed: missed || lostRead.missed, skipped, counterAhead: Boolean(note?.ahead) });
  return { services, rows, read, gaps, note, counts, readBoth: { ...lostRead, readAgain } };
}

function lineSummary(filters: LogFilters, { reply, status, feed, skipped }: ReturnType<typeof useLogFrames>) {
  return {
    note: reply ? counterNote(status && feed.state.status !== "lost" ? status.errors : null, reply, filters, skipped) : null,
    counts: reply ? reasonCounts(reply, status, filters) : null,
  };
}

/** The Line can only be asked once the service log has said which request a report came from. */
function useLogSources(filters: LogFilters) {
  const services = useServiceLogs(filters);
  const report = findReport(filters, services.data?.entries ?? []);
  const lineFilters = lineFiltersFor(filters, report);
  return { services, report, lineFilters, frames: useLogFrames(lineFilters) };
}

function LogsView() {
  const { farms, filters, setFilter, clearFilters } = useLogFilters();
  const now = useNow();
  const { services, report, lineFilters, frames } = useLogSources(filters);
  const { held, setHeld, logs, reply, skipped, missed, feed, lostRead } = frames;
  const { rows, read, gaps, note, counts, readBoth } = useLogRows(lineFilters, services, frames);
  const updated = shownTime(lostRead.missed, logs.updatedAt, feed.state.lastDataAt);

  return (
    <>
      <PageHeader title="Logs" subtitle="Why the production line or a service failed, entry by entry." />

      <ReportTrail filters={filters} report={report} correlationId={lineFilters.correlationId || null} services={services} logs={logs} />
      {reply && counts && readsLine(lineFilters) ? <ReasonCountsPanel counts={counts} note={note} filters={filters} setFilter={setFilter} /> : null}

      <Panel
        title="Entries"
        description={held ? `Held at ${formatTime(held.at)}` : updated ? `Updated ${formatTime(updated)}` : undefined}
        actions={<ConnectionStatus label="Live updates" state={feed.state} now={now} onReconnect={feed.reconnect} />}
      >
        <LogFilterFields filters={filters} farms={farms} onChange={setFilter} onClear={clearFilters} />

        <EntriesNotices logs={logs} reply={reply} feed={feed} skipped={skipped} missed={missed} lostRead={readBoth} shown={rows.length > 0} />
        <ServiceNotices services={services} filters={lineFilters} lineRows={(reply?.entries.length ?? 0) > 0} />

        <EntriesList logs={logs} reply={reply} rows={rows} filters={filters} held={held} setHeld={setHeld} now={now} clearFilters={clearFilters} serviceRead={read} gaps={gaps} />
      </Panel>
    </>
  );
}

export function LogsPage() {
  const { me } = useSession();
  useDocumentTitle("Logs");
  if (!can(me, "ops.admin")) return <Navigate to="/dashboard" replace state={{ notice: `Logs is open to the Admin role. You are signed in as ${me?.user ?? "no one"}.` }} />;
  return <LogsView />;
}
