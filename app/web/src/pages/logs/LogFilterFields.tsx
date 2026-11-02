import type { LogFilters } from "../../api/types";
import { LOG_LEVELS, LOG_REASONS, fromLocalInput, hasFilters, rangeBackwards, toLocalInput } from "../../logic/monitor/logs";
import { Button, Cluster, Grid, Select, TextInput } from "../../ds";

const SOURCE_OPTIONS = [
  { value: "", label: "All sources" },
  { value: "line", label: "Production line" },
];

export function LogFilterFields({ filters, farms, onChange, onClear }: { filters: LogFilters; farms: string[]; onChange: (key: keyof LogFilters, value: string) => void; onClear: () => void }) {
  const reasonOptions = [{ value: "", label: "All reasons" }, ...Object.keys(LOG_REASONS).map((reason) => ({ value: reason, label: reason }))];
  const commitOnEnter = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === "Enter") event.currentTarget.blur();
  };
  return (
    <>
      <Grid min="11rem">
        <Select label="Source" value={filters.source ?? ""} onChange={(e) => onChange("source", e.target.value)} options={SOURCE_OPTIONS} />
        <Select label="Level" value={filters.level} onChange={(e) => onChange("level", e.target.value)} options={[{ value: "", label: "All levels" }, ...LOG_LEVELS]} />
        <Select label="Reason" value={filters.reason} onChange={(e) => onChange("reason", e.target.value)} options={reasonOptions} />
        <Select label="Farm" value={filters.farm} onChange={(e) => onChange("farm", e.target.value)} options={[{ value: "", label: "All farms" }, ...farms.map((f) => ({ value: f, label: f }))]} />
        <TextInput key={`batch-${filters.batch}`} label="Batch" help="Exact number or ID. Applies on Enter." defaultValue={filters.batch} onKeyDown={commitOnEnter} onBlur={(e) => onChange("batch", e.target.value.trim())} />
        <TextInput key={`correlation-${filters.correlationId}`} label="Correlation ID" help="Exact ID. Applies on Enter." defaultValue={filters.correlationId} onKeyDown={commitOnEnter} onBlur={(e) => onChange("correlationId", e.target.value.trim())} />
        <TextInput key={`report-${filters.reportId}`} label="Report ID" help="Exact ID. Applies on Enter." defaultValue={filters.reportId} onKeyDown={commitOnEnter} onBlur={(e) => onChange("reportId", e.target.value.trim())} />
        <TextInput key={`from-${filters.from}`} label="From" type="datetime-local" help="Applies when you leave the field or press Enter." defaultValue={toLocalInput(filters.from)} onKeyDown={commitOnEnter} onBlur={(e) => onChange("from", fromLocalInput(e.target.value))} />
        <TextInput key={`to-${filters.to}`} label="To" type="datetime-local" help="Applies when you leave the field or press Enter." error={rangeBackwards(filters) ? "To is before From, so it is not applied." : null} defaultValue={toLocalInput(filters.to)} onKeyDown={commitOnEnter} onBlur={(e) => onChange("to", fromLocalInput(e.target.value))} />
      </Grid>
      {hasFilters(filters) ? (
        <Cluster gap={2}>
          <Button onClick={onClear}>Clear filters</Button>
        </Cluster>
      ) : null}
    </>
  );
}
