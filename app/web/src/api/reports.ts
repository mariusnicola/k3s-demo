import { newReportId } from "./ids";
import type { ApiError } from "./problem";

export type ReportKind = "console-error" | "socket-failure" | "request-failure" | "render-fault";

export interface Failure {
  kind: ReportKind;
  status: number | null;
  correlationId: string | null;
  message: string | null;
}

const ROUTE_LIMIT = 200;
const MESSAGE_LIMIT = 2000;
const REPEAT_WINDOW_MS = 60_000;
/** Stop fires beforeunload and the page lives on, so silence there would last for good. Long enough for a real leave to reach pagehide. A chosen number, not a measured one. */
const HOLD_MS = 2_000;
/** What React's development build logs for a caught error. Its production build logs the value alone. */
const REACT_DEV_LINE = "%o\n\n%s\n\n%s\n";

export interface BrowserReport extends Failure {
  id: string;
  route: string;
}

const recent = new Map<string, { id: string; at: number }>();
const thrownIds = new WeakMap<object, string>();
/** A primitive has no identity, so its value is the key, for as long as a repeat counts as the same fault. */
const thrownValues = new Map<unknown, { id: string; at: number }>();
const held = new Map<string, BrowserReport>();
let send: ((report: BrowserReport) => void) | null = null;
let holding = false;
let gone = false;
let holdTimer: ReturnType<typeof setTimeout> | null = null;

function forgetOld(now: number): void {
  for (const [key, { at }] of recent) if (now - at > REPEAT_WINDOW_MS) recent.delete(key);
  for (const [value, { at }] of thrownValues) if (now - at > REPEAT_WINDOW_MS) thrownValues.delete(value);
}

function dispatch(key: string, report: BrowserReport): void {
  recent.set(key, { id: report.id, at: Date.now() });
  send?.(report);
}

function stopHolding(): void {
  if (holdTimer !== null) clearTimeout(holdTimer);
  holdTimer = null;
  holding = false;
}

function releaseHeld(): void {
  stopHolding();
  for (const [key, report] of held) dispatch(key, report);
  held.clear();
}

function validStatus(status: number | null): number | null {
  return status !== null && status >= 100 && status <= 599 ? status : null;
}

/** Returns the ID to show. It exists before the post lands, and a repeat shows the ID of the report that was sent. */
export function reportFailure(failure: Failure): string {
  const now = Date.now();
  forgetOld(now);
  const route = window.location.pathname.slice(0, ROUTE_LIMIT);
  // No correlation ID in the key: each try of a repeating failure has its own.
  const key = JSON.stringify([failure.kind, route, failure.status, failure.message]);
  const repeat = recent.get(key)?.id ?? held.get(key)?.id;
  if (repeat) return repeat;
  const id = newReportId();
  // A reload cuts open requests and sockets, and that is no failure of either.
  if (gone || !send) return id;
  const report = { ...failure, id, route, status: validStatus(failure.status), message: failure.message?.slice(0, MESSAGE_LIMIT) ?? null };
  if (holding) held.set(key, report);
  else dispatch(key, report);
  return id;
}

export function reportRequest(error: ApiError): string {
  const place = error.request ? `${error.request.method} ${error.request.url.split("?")[0]}: ` : "";
  return reportFailure({ kind: "request-failure", status: error.status, correlationId: error.correlationId, message: `${place}${error.message}` });
}

/** A value with no way to print still came through console.error, so the failure stays and only its words go. */
function printed(values: unknown[]): string | null {
  try {
    return values.map((value) => (value instanceof Error ? value.message : String(value))).join(" ");
  } catch {
    return null;
  }
}

function isObject(value: unknown): value is object {
  return typeof value === "object" && value !== null;
}

function thrownId(value: unknown): string | undefined {
  if (isObject(value)) return thrownIds.get(value);
  const known = thrownValues.get(value);
  return known && Date.now() - known.at <= REPEAT_WINDOW_MS ? known.id : undefined;
}

function rememberThrown(value: unknown, id: string): void {
  if (isObject(value)) thrownIds.set(value, id);
  else thrownValues.set(value, { id, at: Date.now() });
}

/** A value thrown in the page keeps one ID while it is drawn, and React's console line for it is no second report. A primitive ages out, because the same value later can be a new fault. */
export function reportThrown(error: unknown): string {
  const id = thrownId(error) ?? reportFailure({ kind: "render-fault", status: null, correlationId: null, message: printed([error]) });
  rememberThrown(error, id);
  return id;
}

/** Only the call React makes for that fault. Any other line holding the same words is its own failure. */
function isReactLine(values: unknown[]): boolean {
  if (values.some((value) => isObject(value) && thrownIds.has(value))) return true;
  const logged = values[0] === REACT_DEV_LINE ? values.slice(1, 2) : values;
  return logged.length === 1 && !isObject(logged[0]) && thrownId(logged[0]) !== undefined;
}

function reportConsole(values: unknown[]): void {
  if (isReactLine(values)) return;
  reportFailure({ kind: "console-error", status: null, correlationId: null, message: printed(values) });
}

function watchLeaving(): () => void {
  const onLeaving = () => {
    stopHolding();
    holding = true;
    holdTimer = setTimeout(releaseHeld, HOLD_MS);
  };
  const onGone = () => {
    stopHolding();
    held.clear();
    gone = true;
  };
  const onBack = () => (gone = false);
  window.addEventListener("beforeunload", onLeaving);
  window.addEventListener("pagehide", onGone);
  window.addEventListener("pageshow", onBack);
  return () => {
    window.removeEventListener("beforeunload", onLeaving);
    window.removeEventListener("pagehide", onGone);
    window.removeEventListener("pageshow", onBack);
  };
}

/** Separate from loading this file, so nothing listens or posts until the app itself starts. The sender comes in because the client depends on this file. */
export function startBrowserReports(sender: (report: BrowserReport) => void): () => void {
  const shown = console.error;
  const onError = (event: ErrorEvent) => reportConsole([event.message]);
  const onRejection = (event: PromiseRejectionEvent) => reportConsole([event.reason]);
  send = sender;
  console.error = (...values: unknown[]) => {
    shown.apply(console, values);
    reportConsole(values);
  };
  window.addEventListener("error", onError);
  window.addEventListener("unhandledrejection", onRejection);
  const stopWatching = watchLeaving();
  return () => {
    send = null;
    stopHolding();
    gone = false;
    held.clear();
    recent.clear();
    thrownValues.clear();
    console.error = shown;
    window.removeEventListener("error", onError);
    window.removeEventListener("unhandledrejection", onRejection);
    stopWatching();
  };
}
