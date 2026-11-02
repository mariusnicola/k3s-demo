import type { BatchState, LogEntry } from "../../api/types";
import { STATE_LABELS } from "../../logic/batch/batchRules";
import { formatCount, formatDateTime, formatTime } from "../../logic/format";
import { entryGuidance } from "../../logic/monitor/logs";
import { AppLink, ErrorDetail, IdText, KeyValues, StatusBadge, Text, type Column, type Tone } from "../../ds";

function recorded(value: string | number | null | undefined) {
  return value === null || value === undefined || value === "" ? <Text tone="muted">None recorded</Text> : String(value);
}

function stepLabel(step: string) {
  return STATE_LABELS[step as BatchState] ?? step;
}

function entryWhere(entry: LogEntry): string {
  const source = entry.service ? `Service ${entry.service}, pod ${entry.pod ?? "not recorded"}` : "Production line";
  const batch = entry.batchNumber === null ? "" : `, batch ${formatCount(entry.batchNumber)}`;
  const step = entry.step ? ` before ${stepLabel(entry.step)}` : "";
  const page = entry.route ? `, page ${entry.route}` : "";
  return `${source}${page}${batch}${step}${entry.farm ? ` on ${entry.farm}` : ""}`;
}

function batchName(entry: LogEntry): string {
  return `Batch ${entry.batchNumber === null ? entry.batchId : formatCount(entry.batchNumber)}`;
}

function batchCell(entry: LogEntry) {
  if (entry.batchId && entry.farm) return <AppLink to={`/batches/${encodeURIComponent(entry.farm)}/${encodeURIComponent(entry.batchId)}`}>{batchName(entry)}</AppLink>;
  if (entry.batchNumber === null) return recorded(null);
  return <Text tone="muted">Batch {formatCount(entry.batchNumber)}, no ID recorded</Text>;
}

function TechnicalEntry({ entry }: { entry: LogEntry }) {
  return (
    <KeyValues
      items={[
        { label: "Time", value: formatDateTime(entry.at) },
        ...(entry.service ? [{ label: "Service", value: entry.service }, { label: "Pod", value: recorded(entry.pod) }] : []),
        { label: "Level", value: entry.level },
        { label: "Reason code", value: entry.reason },
        ...(entry.kind ? [{ label: "Report kind", value: entry.kind }] : []),
        { label: "Batch number", value: entry.batchNumber === null ? recorded(null) : formatCount(entry.batchNumber) },
        { label: "Batch ID", value: entry.batchId ? <IdText value={entry.batchId} /> : recorded(null) },
        { label: "Farm", value: recorded(entry.farm) },
        { label: "Next step wanted", value: entry.step ? stepLabel(entry.step) : recorded(null) },
        { label: "Waited", value: entry.waitedMs === null ? recorded(null) : `${formatCount(entry.waitedMs)} ms` },
        { label: "Last HTTP status", value: recorded(entry.httpStatus) },
        { label: "Exception type", value: recorded(entry.exceptionType) },
        { label: "Exception message", value: recorded(entry.exceptionMessage) },
        ...(entry.exceptionStack ? [{ label: "Stack", value: <Text mono>{entry.exceptionStack}</Text> }] : []),
        { label: "Trace ID", value: entry.traceId ? <IdText value={entry.traceId} /> : recorded(null) },
      ]}
    />
  );
}

export function EntryDetails({ entry }: { entry: LogEntry }) {
  const guide = entryGuidance(entry.reason, entry.batchId);
  return <ErrorDetail logsLink explanation={{ what: entry.message, reportId: entry.reportId, where: entryWhere(entry), impact: entry.impact ?? guide?.impact, next: entry.nextAction ?? guide?.next, correlationId: entry.correlationId, technical: <TechnicalEntry entry={entry} /> }} />;
}

export const baseColumns: Column<LogEntry>[] = [
  { key: "time", header: "Time", render: (e) => formatTime(e.at) },
  { key: "source", header: "Source", render: (e) => e.service ?? "Line" },
  { key: "reason", header: "Reason", render: (e) => e.message },
  { key: "batch", header: "Batch", render: batchCell },
  { key: "step", header: "Next step wanted", render: (e) => (e.step ? stepLabel(e.step) : recorded(null)) },
];

const LEVEL_TONE: Record<string, Tone> = { error: "danger", warning: "warning" };

export const levelColumn: Column<LogEntry> = {
  key: "level",
  header: "Level",
  render: (e) => <StatusBadge tone={LEVEL_TONE[e.level] ?? "neutral"}>{e.level}</StatusBadge>,
};
