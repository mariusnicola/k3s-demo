import type { Alert, Batch, Hop, LineLogs, LineStatus, LogEntry, LogFilters, Summary, WorkerPause } from "../../api/types";
import { isHop, isRecord } from "./hops";
import { rangeBackwards } from "../monitor/logs";

export interface BatchRow {
  etag: string;
  batch: Batch;
}

export type HopFrame =
  | { type: "snapshot"; at: string; hops: Hop[]; summary: Summary; alerts: Alert[]; batches: BatchRow[]; capped: boolean; unread?: boolean; pause?: WorkerPause | null }
  | { type: "pause"; pause: WorkerPause }
  | { type: "hops"; hops: Hop[] }
  | ({ type: "batch" } & BatchRow)
  | { type: "summary"; summary: Summary; capped: boolean; unread?: boolean }
  | { type: "alerts"; alerts: Alert[] };

export interface LineFrame {
  type: "snapshot" | "line";
  at: string;
  status: LineStatus;
  entries: LogEntry[];
  entriesDropped: number;
  evicted: number;
}

export interface HopState {
  summary: Summary | null;
  alerts: Alert[] | null;
  /** Alerts sent before the store was first read. They are empty for that reason, not because none are open. */
  unreadAlerts: Alert[] | null;
  /** The scan hit its cap on open rows, so pending, blocked, flagged and alerts may read short. */
  capped: boolean;
  rows: Map<string, BatchRow>;
  pause: WorkerPause | null;
}

export function emptyHopState(): HopState {
  return { summary: null, alerts: null, unreadAlerts: null, capped: false, rows: new Map(), pause: null };
}

/** Frames reach every batch ever touched, so the page keeps only what it can render and merge cheaply. */
const ROWS_KEPT = 1_000;

export function rowKey(batch: { farmId: string; id: string }): string {
  return `${batch.farmId}/${batch.id}`;
}

function readJson(raw: string): Record<string, unknown> | null {
  try {
    const value: unknown = JSON.parse(raw);
    return isRecord(value) ? value : null;
  } catch {
    return null;
  }
}

function isRow(value: unknown): value is BatchRow {
  return isRecord(value) && typeof value.etag === "string" && isRecord(value.batch) && typeof value.batch.id === "string" && typeof value.batch.farmId === "string";
}

/** A null `reconciledAt` means the store was never read, so the zeros in the frame mean nothing. */
function unread(value: Record<string, unknown>): { unread?: boolean } {
  return value.reconciledAt === null ? { unread: true } : {};
}

function isPause(value: unknown): value is WorkerPause {
  return isRecord(value) && typeof value.paused === "boolean" && typeof value.reason === "string" && typeof value.at === "string";
}

export function parseHopFrame(raw: string): HopFrame | null {
  const value = readJson(raw);
  if (!value) return null;
  if (value.type === "pause") return isPause(value) ? { type: "pause", pause: { paused: value.paused, reason: value.reason, pod: value.pod, at: value.at } } : null;
  if (value.type === "hops") return Array.isArray(value.hops) ? { type: "hops", hops: value.hops.filter(isHop) } : null;
  if (value.type === "batch") return isRow(value) ? { type: "batch", etag: value.etag, batch: value.batch } : null;
  if (value.type === "summary") return isRecord(value.summary) ? { type: "summary", summary: value.summary as unknown as Summary, capped: value.capped === true, ...unread(value) } : null;
  if (value.type === "alerts") return Array.isArray(value.alerts) ? { type: "alerts", alerts: value.alerts as Alert[] } : null;
  if (value.type !== "snapshot" || typeof value.at !== "string" || !Array.isArray(value.hops) || !isRecord(value.summary) || !Array.isArray(value.alerts) || !Array.isArray(value.batches)) return null;
  return { type: "snapshot", at: value.at, hops: value.hops.filter(isHop), summary: value.summary as unknown as Summary, alerts: value.alerts as Alert[], pause: isPause(value.pause) ? value.pause : null, batches: value.batches.filter(isRow), capped: value.capped === true, ...unread(value) };
}

export function parseLineFrame(raw: string): LineFrame | null {
  const value = readJson(raw);
  if (!value || (value.type !== "snapshot" && value.type !== "line") || typeof value.at !== "string" || !isRecord(value.status) || !Array.isArray(value.entries)) return null;
  return {
    type: value.type,
    at: value.at,
    status: value.status as unknown as LineStatus,
    entries: value.entries as LogEntry[],
    entriesDropped: typeof value.entriesDropped === "number" ? value.entriesDropped : 0,
    evicted: typeof value.evicted === "number" ? value.evicted : 0,
  };
}

/** Frames can arrive out of order, so the row with the highest version wins. Changes the map in place: copying it per frame costs a thousand writes each. */
function keepRow(rows: Map<string, BatchRow>, row: BatchRow): boolean {
  const key = rowKey(row.batch);
  const known = rows.get(key);
  if (known && known.batch.version >= row.batch.version) return false;
  rows.delete(key);
  rows.set(key, row);
  const oldest = rows.keys().next().value;
  if (rows.size > ROWS_KEPT && oldest !== undefined) rows.delete(oldest);
  return true;
}

export function applyHopFrame(state: HopState, frame: HopFrame): HopState {
  if (frame.type === "hops") return state;
  if (frame.type === "pause") return { ...state, pause: frame.pause };
  if (frame.type === "summary") return frame.unread ? { ...state, summary: null } : { ...state, capped: frame.capped, summary: frame.summary, alerts: state.alerts ?? state.unreadAlerts, unreadAlerts: null };
  if (frame.type === "alerts") return state.unreadAlerts !== null ? { ...state, unreadAlerts: frame.alerts } : { ...state, alerts: frame.alerts };
  if (frame.type === "batch") return keepRow(state.rows, frame) ? { ...state } : state;
  const rows = new Map(frame.batches.map((row) => [rowKey(row.batch), row]));
  const { capped } = frame;
  const pause = frame.pause ?? null;
  return frame.unread ? { summary: null, alerts: null, unreadAlerts: frame.alerts, capped, rows, pause } : { pause, summary: frame.summary, alerts: frame.alerts, unreadAlerts: null, capped, rows };
}

export function newestFirst(batches: Batch[]): Batch[] {
  return batches.sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}

/**
 * `matches` is the server filter of the list, so a row that moved out of it leaves and a new one that fits joins.
 * With the socket lost the rows are frozen and may be older than the read, so a missing one has left, not arrived.
 */
export function mergeBatches(list: readonly Batch[], rows: ReadonlyMap<string, BatchRow>, matches: (batch: Batch) => boolean, limit: number, addMissing = true): Batch[] {
  const merged = new Map(list.map((batch) => [rowKey(batch), batch]));
  for (const [key, { batch }] of rows) {
    const known = merged.get(key);
    if (known && known.version >= batch.version) continue;
    if (!matches(batch)) merged.delete(key);
    else if (known || addMissing) merged.set(key, batch);
  }
  return newestFirst([...merged.values()]).slice(0, limit);
}

export function logKey(entry: LogEntry): string {
  const source = entry.service ? `|${entry.service}|${entry.pod ?? ""}|${entry.message}` : "";
  return `${entry.at}|${entry.reason}|${entry.batchNumber}|${entry.step}|${entry.traceId ?? ""}${source}`;
}

/** Same test the line applies to GET /line/logs, so a pushed entry lands where a read would put it. */
function matchesLog(entry: LogEntry, filters: LogFilters): boolean {
  const at = Date.parse(entry.at);
  return (
    (!filters.level || entry.level === filters.level) &&
    (!filters.reason || entry.reason === filters.reason) &&
    (!filters.farm || entry.farm === filters.farm) &&
    (!filters.batch || entry.batchId === filters.batch || String(entry.batchNumber) === filters.batch) &&
    (!filters.correlationId || entry.correlationId === filters.correlationId) &&
    (!filters.reportId || entry.reportId === filters.reportId) &&
    (!filters.from || at >= Date.parse(filters.from)) &&
    (!filters.to || rangeBackwards(filters) || at <= Date.parse(filters.to))
  );
}

/** A snapshot holds the newest entries only, so one that starts after the list means an outage outran it. */
export function logsGap(logs: LineLogs, frame: LineFrame): boolean {
  const newest = logs.entries[0];
  const oldest = frame.entries.at(-1);
  return frame.type === "snapshot" && frame.entriesDropped > 0 && newest !== undefined && oldest !== undefined && Date.parse(oldest.at) > Date.parse(newest.at);
}

export function mergeLogs(logs: LineLogs, frame: LineFrame, filters: LogFilters, limit: number): LineLogs {
  const listed = new Set(logs.entries.map(logKey));
  // A full list was cut at its oldest row, so an entry older than that is a repeat from a snapshot, not news.
  const floor = logs.droppedByLimit > 0 ? logs.entries.at(-1) : undefined;
  const isNew = (entry: LogEntry) => !listed.has(logKey(entry)) && (!floor || Date.parse(entry.at) >= Date.parse(floor.at));
  const fresh = frame.entries.filter((entry) => matchesLog(entry, filters) && isNew(entry));
  const byReason = { ...logs.byReason };
  for (const entry of fresh) byReason[entry.reason] = (byReason[entry.reason] ?? 0) + 1;
  const entries = [...fresh, ...logs.entries].slice(0, limit);
  const matched = logs.matched + fresh.length;
  return { matched, byReason, droppedByLimit: matched - entries.length, evicted: frame.evicted, entries };
}
