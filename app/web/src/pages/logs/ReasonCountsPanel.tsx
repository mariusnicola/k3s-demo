import type { LogFilters } from "../../api/types";
import { LOG_REASONS, counterNote, reasonCounts } from "../../logic/monitor/logs";
import { Banner, CountFilterList, Panel, Text } from "../../ds";

function reasonSentence(reason: string): string {
  return LOG_REASONS[reason] ?? reason;
}

interface ReasonCountsPanelProps {
  counts: NonNullable<ReturnType<typeof reasonCounts>>;
  note: ReturnType<typeof counterNote> | null;
  filters: LogFilters;
  setFilter: (key: keyof LogFilters, value: string) => void;
}

export function ReasonCountsPanel({ counts, note, filters, setFilter }: ReasonCountsPanelProps) {
  return (
    <Panel title="Count by reason" description={`${counts.sinceStart ? "Since the line started." : "Entries in memory that match these filters."} Press a reason to show only its entries.`}>
      {counts.items.length > 0 ? (
        <CountFilterList
          label="Count per reason"
          items={counts.items.map((r) => ({ value: r.reason, title: reasonSentence(r.reason), detail: r.reason, count: r.count }))}
          selected={filters.reason}
          onSelect={(reason) => setFilter("reason", filters.reason === reason ? "" : reason)}
        />
      ) : (
        <Text tone="secondary">No reason to show for these filters.</Text>
      )}
      {note ? (
        note.matches ? (
          <Text size="xs" tone="secondary" as="p">
            {note.text}
          </Text>
        ) : (
          <Banner tone="info" title="Why this differs from the Dashboard" live="off">
            {note.text}
          </Banner>
        )
      ) : null}
    </Panel>
  );
}
