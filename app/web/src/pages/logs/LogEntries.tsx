import type { Dispatch, SetStateAction } from "react";
import type { LineLogs, LogEntry, LogFilters } from "../../api/types";
import type { Resource } from "../../live/useResource";
import { logKey } from "../../logic/feed/live";
import { formatCount, formatTime } from "../../logic/format";
import type { ServiceRead } from "../../logic/monitor/listGaps";
import { hasFilters } from "../../logic/monitor/logs";
import { Button, DataTable, EmptyState, Skeleton, Text } from "../../ds";
import { ProblemState } from "../shared";
import { EntryDetails, baseColumns, levelColumn } from "./logColumns";
import type { HeldLogs } from "./useLogFrames";

interface EntriesTableProps {
  reply: LineLogs | null;
  rows: LogEntry[];
  filters: LogFilters;
  held: HeldLogs | null;
  setHeld: Dispatch<SetStateAction<HeldLogs | null>>;
  now: number;
}

function EntriesTable({ reply, rows, filters, held, setHeld, now }: EntriesTableProps) {
  const columns = filters.level ? baseColumns : [levelColumn, ...baseColumns];
  return (
    <>
      <DataTable
        caption="Log entries"
        columns={columns}
        rows={rows}
        rowKey={logKey}
        onHold={(holding) => setHeld(holding ? (current) => current ?? { rows, at: now } : null)}
        detail={{ header: "Details", label: (e) => `details of ${e.reason} at ${formatTime(e.at)}`, render: (e) => <EntryDetails entry={e} /> }}
      />
      {held ? (
        <Text size="xs" tone="secondary" as="p">
          List held while the pointer or focus is inside it.
        </Text>
      ) : null}
      {reply && reply.droppedByLimit > 0 ? (
        <Text size="xs" tone="secondary" as="p">
          Showing the newest {formatCount(reply.entries.length)} of {formatCount(reply.matched)} matching entries. Narrow the filters to reach the rest.
        </Text>
      ) : null}
    </>
  );
}

function NoEntries({ filters, clearFilters }: { filters: LogFilters; clearFilters: () => void }) {
  return hasFilters(filters) ? (
    <EmptyState title="No entries match these filters" action={<Button onClick={clearFilters}>Clear filters</Button>}>
      Change a filter, or clear all of them.
    </EmptyState>
  ) : (
    <EmptyState icon="check" title="Nothing logged yet">
      Entries appear here when the production line records something.
    </EmptyState>
  );
}

/** An empty list claims nothing happened, so it may only say so when no notice beside it says otherwise. */
function NoEntriesKnown({ gaps }: { gaps: string[] }) {
  return <EmptyState title="No entries to show">{gaps.join(" ")} So this list may be missing entries.</EmptyState>;
}

interface EntriesListProps extends EntriesTableProps {
  logs: Resource<LineLogs>;
  serviceRead: ServiceRead;
  gaps: string[];
  clearFilters: () => void;
}

export function EntriesList({ logs, reply, rows, filters, held, setHeld, now, clearFilters, serviceRead, gaps }: EntriesListProps) {
  return (
    logs.error && !reply && rows.length === 0 ? (
      <ProblemState error={logs.error} onRetry={logs.reload} />
    ) : (!reply && !logs.error) || (rows.length === 0 && serviceRead === "pending") ? (
      <Skeleton rows={6} label="Loading log entries" />
    ) : rows.length === 0 ? (
      gaps.length > 0 ? <NoEntriesKnown gaps={gaps} /> : <NoEntries filters={filters} clearFilters={clearFilters} />
    ) : (
      <EntriesTable reply={reply} rows={rows} filters={filters} held={held} setHeld={setHeld} now={now} />
    )
  );
}
