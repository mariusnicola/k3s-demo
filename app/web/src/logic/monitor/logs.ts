import type { LineLogs, LineStatus, LogEntry, LogFilters } from "../../api/types";
import { formatCount } from "../format";

export const LOG_LEVELS = [
  { value: "error", label: "Errors" },
  { value: "info", label: "Information" },
];

export const BROWSER_REASON = "browser-failure";

/** The services refuse any other code they could be sent, so this is the whole list. They send no sentence per reason; these are the plain ones. */
export const LOG_REASONS: Record<string, string> = {
  "event-timeout": "The Line gave up waiting for the previous event of a batch.",
  "http-refused": "The API refused a request from the Line.",
  "network-failure": "The Line could not reach the API.",
  "login-failure": "The Line could not sign in.",
  unknown: "Something unexpected failed in the Line.",
  started: "A batch was started.",
  closed: "A batch reached Closed.",
  rejected: "A batch was rejected at inspection.",
  [BROWSER_REASON]: "A browser reported a failure.",
};

const STOPPED = "The Line dropped this batch, which stays at the step it reached.";

/** The service sends no impact or next step per reason either, so they live beside the sentences. */
const LOG_GUIDANCE: Record<string, { impact: string; next: string }> = {
  "event-timeout": { impact: "The batch is not failed and its event may finish later. If the event is dead, the batch is blocked until it is replayed.", next: "Open the batch and check its event." },
  "http-refused": { impact: STOPPED, next: "Read the API's answer above, then open the batch." },
  "network-failure": { impact: `${STOPPED} The request may or may not have arrived.`, next: "Check that the API is up, then open the batch." },
  "login-failure": { impact: "The Line could not walk this batch.", next: "Check that the API is up and the Line's user can sign in." },
  unknown: { impact: STOPPED, next: "Read the full message above." },
  [BROWSER_REASON]: { impact: "A browser reported this failure. The report changes nothing on any batch.", next: "Open the report by its ID to see the request it came from." },
};

export function logGuidance(reason: string): { impact: string; next: string } | null {
  return LOG_GUIDANCE[reason] ?? null;
}

const NO_ID = "No batch ID came back, so there is nothing to open.";
const AT_CREATE: Record<string, { impact: string; next: string }> = {
  "http-refused": { impact: NO_ID, next: "Read the API's answer above." },
  "network-failure": { impact: "No batch ID came back. The request may have arrived, so a batch may exist that the Line will not walk.", next: "Look on the Batches page for a new batch at Created." },
  unknown: { impact: NO_ID, next: "Read the full message above." },
};

/** A walk that failed at Create never learned its batch ID, so words about that batch would send the operator nowhere. */
export function entryGuidance(reason: string, batchId: string | null | undefined): { impact: string; next: string } | null {
  return (!batchId && AT_CREATE[reason]) || logGuidance(reason);
}

/** The dashboard counter link. Lives here so the counter and the page cannot drift apart. */
export const LINE_ERRORS_PATH = "/logs?level=error&source=line";

const FILTER_KEYS = ["level", "reason", "farm", "batch", "correlationId", "reportId", "from", "to"] as const;

const REPORT_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function validDate(value: string | null): string {
  return value && !Number.isNaN(Date.parse(value)) ? value : "";
}

function reportIdFrom(params: URLSearchParams): string {
  const id = (params.get("reportId") ?? "").trim();
  return REPORT_ID.test(id) ? id : "";
}

/** A hand-edited address must not reach the service as a 400 with no way out. */
export function readLogFilters(params: URLSearchParams, farms: readonly string[]): LogFilters {
  const level = params.get("level") ?? "";
  const reason = params.get("reason") ?? "";
  const farm = params.get("farm") ?? "";
  return {
    source: params.get("source") === "line" ? "line" : "",
    level: LOG_LEVELS.some((l) => l.value === level) ? level : "",
    reason: reason in LOG_REASONS ? reason : "",
    farm: farms.includes(farm) ? farm : "",
    batch: (params.get("batch") ?? "").trim(),
    correlationId: (params.get("correlationId") ?? "").trim(),
    reportId: reportIdFrom(params),
    from: validDate(params.get("from")),
    to: validDate(params.get("to")),
  };
}

/** The service answers 400 to this, so the field says it and the read goes out without `to`. */
export function rangeBackwards(filters: LogFilters): boolean {
  return Boolean(filters.from && filters.to) && Date.parse(filters.from) > Date.parse(filters.to);
}

export function logQuery(filters: LogFilters, limit: number): string {
  const query = new URLSearchParams();
  for (const key of FILTER_KEYS) if (filters[key] && !(key === "to" && rangeBackwards(filters))) query.set(key, filters[key]);
  query.set("limit", String(limit));
  return query.toString();
}

/** The service log holds warnings and errors under its own reasons, and answers 400 to the Line's. */
export function readsServiceLog(filters: LogFilters): boolean {
  return filters.source !== "line" && filters.level !== "info" && (!filters.reason || filters.reason === BROWSER_REASON);
}

/** The Line answers 400 to a reason it does not hold, and it holds no report. */
export function readsLine(filters: LogFilters): boolean {
  return !filters.reportId && filters.reason !== BROWSER_REASON;
}

export function withServiceEntries(line: LogEntry[], service: LogEntry[]): LogEntry[] {
  if (service.length === 0) return line;
  return [...line, ...service].sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
}

export function logsFor(correlationId: string): string {
  return `/logs?correlationId=${encodeURIComponent(correlationId)}`;
}

export function logsForReport(reportId: string): string {
  return `/logs?reportId=${encodeURIComponent(reportId)}`;
}

export function findReport(filters: LogFilters, entries: LogEntry[]): LogEntry | null {
  return (filters.reportId && entries.find((entry) => entry.reportId === filters.reportId)) || null;
}

/** The Line knows no report, only the correlation ID the report holds. Until that is known the filters keep the report, which no Line row can match. */
export function lineFiltersFor(filters: LogFilters, report: LogEntry | null): LogFilters {
  const correlationId = filters.correlationId || report?.correlationId;
  return filters.reportId && correlationId ? { ...filters, reportId: "", correlationId } : filters;
}

export function hasFilters(filters: LogFilters): boolean {
  return Boolean(filters.source) || FILTER_KEYS.some((key) => filters[key]);
}

/** A `datetime-local` field works in local time with no zone; the address and the service use UTC. */
export function toLocalInput(iso: string): string {
  if (!iso) return "";
  const date = new Date(iso);
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
}

export function fromLocalInput(value: string): string {
  return value ? new Date(value).toISOString() : "";
}

function narrowed(filters: LogFilters): boolean {
  return Boolean(filters.farm || filters.batch || filters.correlationId || filters.reportId || filters.from || filters.to);
}

export interface ReasonCounts {
  /** True when the counts are the line's own totals, which outlive the entries in memory. */
  sinceStart: boolean;
  items: { reason: string; count: number }[];
}

export function reasonCounts(logs: LineLogs, status: LineStatus | null, filters: LogFilters): ReasonCounts {
  const sinceStart = status !== null && filters.level === "error" && !narrowed(filters);
  const source = status !== null && sinceStart ? status.errorsByReason : logs.byReason;
  const items = Object.entries(source)
    .filter(([, count]) => count > 0)
    .map(([reason, count]) => ({ reason, count }))
    .sort((a, b) => b.count - a.count);
  return { sinceStart, items };
}

export interface CounterNote {
  matches: boolean;
  text: string;
  /** The counter holds more than the list, and nothing on the page explains why. */
  ahead?: boolean;
}

function errorsWord(count: number): string {
  return `${formatCount(count)} ${count === 1 ? "error" : "errors"}`;
}

/** The dashboard counts every error since the line started; memory keeps the newest entries of every level, so errors can leave it. */
export function counterNote(errors: number | null, logs: LineLogs, filters: LogFilters, skipped = 0): CounterNote {
  if (errors === null) return { matches: false, text: "The Dashboard counter could not be read, so the two cannot be compared." };
  if (filters.level !== "error" && !logGuidance(filters.reason)) return { matches: false, text: "The Dashboard counts errors only, and this view is not limited to errors." };
  if (errors === logs.matched) return { matches: true, text: `Matches the Dashboard: ${errorsWord(errors)}.` };
  if (filters.reason || narrowed(filters)) {
    const rest = logs.evicted > 0 ? "The filters hide the rest, or they left memory." : "The filters hide the rest.";
    return { matches: false, text: `Showing ${formatCount(logs.matched)} of the ${errorsWord(errors)} on the Dashboard. ${rest}` };
  }
  if (skipped > 0) return { matches: false, text: `The Dashboard says ${errorsWord(errors)} and this log holds ${formatCount(logs.matched)}. Some entries were left out of this list under load.` };
  const gone = errors - logs.matched;
  if (gone > 0 && gone <= logs.evicted) {
    return { matches: false, text: `The Dashboard counts ${errorsWord(errors)} since the line started. ${formatCount(gone)} of them left memory, so ${formatCount(logs.matched)} can be listed.` };
  }
  if (gone < 0) {
    return { matches: false, text: `The Dashboard says ${errorsWord(errors)} and this log holds ${formatCount(logs.matched)}. The Line may have restarted, and these entries are from before it.` };
  }
  return { matches: false, ahead: true, text: `The Dashboard says ${errorsWord(errors)} and this log holds ${formatCount(logs.matched)}.` };
}
