import type { ChangeEntry } from "../../../api/monitorTypes";
import { useChanges } from "../../../live/monitorFeed";
import { formatTime } from "../../../logic/format";
import { DataTable, EmptyState, Panel, Skeleton } from "../../../ds";

const CHANGES_SHOWN = 50;

function ChangesTable({ rows }: { rows: ChangeEntry[] }) {
  return (
    <DataTable
      caption="Changes"
      columns={[
        { key: "time", header: "Time", render: (c) => formatTime(c.time) },
        { key: "kind", header: "Change", render: (c) => c.kind },
        { key: "object", header: "Object", render: (c) => c.object },
        { key: "old", header: "From", render: (c) => c.old ?? "" },
        { key: "new", header: "To", render: (c) => c.new ?? "" },
      ]}
      rows={rows}
      rowKey={(c) => `${c.time} ${c.kind} ${c.object} ${c.new}`}
    />
  );
}

function changesWord(status: string, stale: boolean, shown: number, total: number): string {
  if (status === "lost") return "The monitor is not answering. Last known changes.";
  if (stale) return "The monitor stopped reporting. Last known changes.";
  return status === "open" ? `Newest ${shown} of ${total}, live.` : "Waiting for the monitor.";
}

export function ChangesPanel({ stale }: { stale: boolean }) {
  const { changes, status } = useChanges();
  const newest = [...changes].sort((a, b) => Date.parse(b.time) - Date.parse(a.time)).slice(0, CHANGES_SHOWN);
  const description = changesWord(status, stale, newest.length, changes.length);
  return (
    <Panel title="Changes" description={description}>
      {newest.length > 0 ? (
        <ChangesTable rows={newest} />
      ) : status === "lost" || stale ? (
        <EmptyState icon="info" title="Unknown">
          The monitor is not answering. See the Health panel.
        </EmptyState>
      ) : status === "connecting" ? (
        <Skeleton rows={3} label="Waiting for the monitor" />
      ) : (
        <EmptyState icon="info" title="No changes yet">
          Nothing has changed since the monitor started.
        </EmptyState>
      )}
    </Panel>
  );
}
