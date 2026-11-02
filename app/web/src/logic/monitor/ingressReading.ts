import type { IngressLogs, IngressRow, LogEntry, LogFilters } from "../../api/types";
import { REQUEST_LIMIT_MS } from "../../config";
import { MAX_ATTEMPTS, MAX_DELAY_MS } from "../backoff";

export interface TrailFacts {
  /** The report asked for, if any. */
  reportId: string | null;
  report: LogEntry | null;
  correlationId: string | null;
  /** The filters leave the service log out, so a report cannot be looked up. */
  serviceSkipped: boolean;
  serviceFailed: boolean;
  /** Some filter besides the IDs is on, so a read holds only part of its log. */
  narrowed: boolean;
  /** Browser rows have their own queue, so only these can have taken the report. */
  reportsEvicted: boolean;
  /** Some read has not answered yet, so a reading now could flip. */
  pending: boolean;
  ingress: IngressLogs | null;
  ingressFailed: boolean;
  /** A service or the Line logged this ID. The report's own row does not count. */
  reachedService: boolean;
  /** False when a read failed, was narrowed or came back short: then no row in it proves nothing. */
  everyLogRead: boolean;
}

/** A report lands after the request gave up, so this is how long before it the request can have started. */
const LONGEST_REQUEST_MS = MAX_ATTEMPTS * REQUEST_LIMIT_MS + (MAX_ATTEMPTS - 1) * MAX_DELAY_MS;

/** The IDs pick out one request and hide nothing about it, unless they name two: the service log applies both. Any other filter hides rows. */
export function narrowsRead(filters: LogFilters, report: LogEntry | null): boolean {
  const otherRequest = Boolean(filters.reportId && filters.correlationId) && filters.correlationId !== report?.correlationId;
  return otherRequest || Boolean(filters.source || filters.level || filters.reason || filters.farm || filters.batch || filters.from || filters.to);
}

export const REACHED_SERVICE = "The request reached a service.";
export const STOPPED = "The request stopped at the ingress.";
export const NEVER_ARRIVED = "The request never arrived at the ingress.";
export const NOT_FOLLOWED = "Unknown: the ingress log is not being followed, so this cannot say whether the request arrived.";
export const INGRESS_UNREAD = "Unknown: the ingress log could not be read.";
export const TOO_OLD = "Too old to tell: the ingress log may not reach back to this request.";
export const OTHER_WAY = "No ingress row for this ID, but a service logged it, so it came by another way.";
export const NOT_ALL_READ = "No ingress row for this ID, but not every log was read in full, so this cannot say whether it arrived.";
export const NO_TIME = "No ingress row for this ID, and no failure time to hold against the start of the log.";
export const NOTHING_TO_MATCH = "This report has no correlation ID, so there is nothing to match on.";
export const NO_REPORT = "No report with this ID is in the service log. A report is sent once, so it may have been lost.";
export const REPORT_HIDDEN = "Unknown: these filters may hide the report. Clear them to look it up.";
export const REPORT_EVICTED = "Unknown: older browser reports left memory, and this one may be among them.";
export const SERVICE_LEFT_OUT = "Unknown: these filters leave out the service log, so the report cannot be looked up.";
export const SERVICE_UNREAD = "Unknown: the service log could not be read, so the report cannot be looked up.";

/** Compared as text: nginx writes one group per try, and a group is only `-` when no upstream was reached. */
function noUpstreamAnswered({ upstreamStatus }: IngressRow): boolean {
  return upstreamStatus !== null && upstreamStatus.split(/,| : /).every((group) => group.trim() === "-");
}

/**
 * Never says a service answered: nginx also records 502 and 504 for a connect that failed. A row older than the field says nothing.
 * Ingress time is whole seconds, and what order rows of one second arrive in is no part of the contract, so such rows name no last one.
 */
function arrived(rows: IngressRow[], newest: IngressRow): string {
  const lastSecond = rows.filter((row) => Date.parse(row.at) === Date.parse(newest.at));
  if (lastSecond.length > 1) {
    const statuses = lastSecond.map(({ status, upstreamStatus }) => (upstreamStatus === null ? status : `${status} (upstream ${upstreamStatus})`));
    return `A request with this ID arrived at the ingress, with statuses ${statuses.join(", ")} in its last second.`;
  }
  const upstream = newest.upstreamStatus === null ? "" : ` and upstream status ${newest.upstreamStatus}`;
  return `A request with this ID arrived at the ingress, last with status ${newest.status}${upstream}.`;
}

function rowReading(rows: IngressRow[], newest: IngressRow, reachedService: boolean): string {
  if (reachedService) return REACHED_SERVICE;
  return rows.every(noUpstreamAnswered) ? STOPPED : arrived(rows, newest);
}

function noRowReading({ ingress, report, reachedService, everyLogRead }: TrailFacts): string {
  const since = ingress?.since ?? null;
  const failedAt = report?.at ?? null;
  if (since === null || (failedAt !== null && Date.parse(failedAt) - LONGEST_REQUEST_MS < Date.parse(since))) return TOO_OLD;
  if (reachedService) return OTHER_WAY;
  if (!everyLogRead) return NOT_ALL_READ;
  return failedAt === null ? NO_TIME : NEVER_ARRIVED;
}

function missingReport({ serviceSkipped, serviceFailed, narrowed, reportsEvicted }: TrailFacts): string {
  if (serviceSkipped) return SERVICE_LEFT_OUT;
  if (serviceFailed) return SERVICE_UNREAD;
  if (narrowed) return REPORT_HIDDEN;
  return reportsEvicted ? REPORT_EVICTED : NO_REPORT;
}

/** One sentence, and only what the reads support. Null while they cannot support one yet. */
export function ingressReading(facts: TrailFacts): string | null {
  if (facts.pending) return null;
  if (facts.reportId && !facts.report) return missingReport(facts);
  if (!facts.correlationId) return NOTHING_TO_MATCH;
  if (facts.ingressFailed) return INGRESS_UNREAD;
  if (!facts.ingress) return null;
  if (!facts.ingress.available) return NOT_FOLLOWED;
  const [newest] = facts.ingress.rows;
  if (!newest) return noRowReading(facts);
  return rowReading(facts.ingress.rows, newest, facts.reachedService);
}
