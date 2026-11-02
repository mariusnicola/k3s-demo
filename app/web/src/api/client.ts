import { API_BASE, LINE_URL, LIVE_URL, LOG_LIMIT, REQUEST_LIMIT_MS } from "../config";
import { retryAfterMs } from "../logic/backoff";
import { batchQuery, type BatchQuery } from "../logic/batch/batchQuery";
import { logQuery } from "../logic/monitor/logs";
import type { Bucket, MetricsReply } from "../logic/monitor/metrics";
import { ApiError, STATUS_NETWORK, type ProblemRequest, type ServerWords } from "./problem";
import { newCorrelationId } from "./ids";
import { reportRequest, type BrowserReport } from "./reports";
import { clearRetry, nextWait, noteRetry, retryWait } from "./retry";
import type { Batch, BatchState, ComplianceResult, IngressLogs, LineLogs, LogFilters, Me, ServiceLogs } from "./types";

let onUnauthorized: () => void = () => undefined;

export function setUnauthorizedHandler(handler: () => void): void {
  onUnauthorized = handler;
}

export function endSession(): void {
  onUnauthorized();
}

export interface Tagged<T> {
  data: T;
  etag: string | null;
}

interface RequestOptions {
  method?: string;
  body?: unknown;
  ifMatch?: string;
  signal?: AbortSignal;
  /** Opts out of a lost write reading as unknown. Only for a call whose repeat changes no batch and no line state. */
  safeToRepeat?: boolean;
  /** Statuses this call's caller reads as an answer: no wait, no report, and a 401 here does not end the session. */
  answers?: number[];
}

/** The default is the safe one, so a write added later cannot forget to say it. */
function lostResultUnknown(options: RequestOptions): boolean {
  return (options.method ?? "GET") !== "GET" && !options.safeToRepeat;
}

const UNANSWERED = new Set([502, 504, STATUS_NETWORK]);

/** An ordinary 4xx is the server answering, and reporting those would bury the failures nobody can explain. */
function isFailure(error: ApiError, options: RequestOptions): boolean {
  return (error.status === STATUS_NETWORK || error.status >= 500) && !options.answers?.includes(error.status);
}

/** Here and not per attempt: a read that recovers on a retry never failed. */
async function send<T>(url: string, options: RequestOptions = {}): Promise<Tagged<T>> {
  try {
    return await attempts<T>(url, options);
  } catch (error) {
    if (error instanceof ApiError && isFailure(error, options)) error.reportId = reportRequest(error);
    throw error;
  }
}

/** The one retry path. Only reads go through it: a repeated write could land twice. */
async function attempts<T>(url: string, options: RequestOptions): Promise<Tagged<T>> {
  const correlationId = newCorrelationId();
  if (options.method) return once<T>(url, options, correlationId);
  const id = Symbol(url);
  try {
    for (let attempt = 1; ; attempt += 1) {
      try {
        return await once<T>(url, options, correlationId);
      } catch (error) {
        const ms = error instanceof ApiError && options.answers?.includes(error.status) ? null : nextWait(error, attempt);
        if (ms === null) throw error;
        noteRetry(id, { attempt: attempt + 1, nextInMs: (error as ApiError).retryAfterMs });
        await retryWait(ms, options.signal);
      }
    }
  } finally {
    clearRetry(id);
  }
}

function requestHeaders(options: RequestOptions, correlationId: string): Record<string, string> {
  const headers: Record<string, string> = { Accept: "application/json", "X-Correlation-Id": correlationId };
  if (options.body !== undefined) headers["Content-Type"] = "application/json";
  if (options.ifMatch) headers["If-Match"] = options.ifMatch;
  return headers;
}

/** Only text the server really sent counts as its words. */
function words(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value : null;
}

/** Only an abort or the time limit rejects with this type, and either means no answer. A bad or cut body still leaves the status. */
function bodyProblem(cause: unknown, options: RequestOptions, request: ProblemRequest, sent: string): null {
  if (cause instanceof DOMException) throw noAnswer(cause, options, request, sent, true);
  return null;
}

async function refusal(response: Response, options: RequestOptions, request: ProblemRequest, sent: string): Promise<ApiError> {
  // The series endpoint explains itself in `error`, not in a problem body.
  const problem = (await response.json().catch((cause: unknown) => bodyProblem(cause, options, request, sent))) as ({ title?: string; detail?: string; error?: string; correlationId?: string } & Record<keyof ServerWords, unknown>) | null;
  return new ApiError(response.status, problem?.title ?? response.statusText, problem?.detail ?? problem?.error ?? null, {
    resultUnknown: lostResultUnknown(options) && UNANSWERED.has(response.status),
    noReason: !problem?.title && !problem?.detail && !problem?.error,
    retryAfterMs: retryAfterMs(response.headers.get("Retry-After")),
    correlationId: problem?.correlationId ?? response.headers.get("X-Correlation-Id") ?? sent,
    request,
    said: { where: words(problem?.where), impact: words(problem?.impact), next: words(problem?.next) },
  });
}

/** A cut body is no answer either: for a write, the headers arriving do not say what the body would have. */
function noAnswer(cause: unknown, options: RequestOptions, request: ProblemRequest, correlationId: string, answerBegan: boolean): unknown {
  if (cause instanceof DOMException && cause.name === "AbortError") return cause;
  const timedOut = cause instanceof DOMException && cause.name === "TimeoutError";
  return new ApiError(STATUS_NETWORK, "Network error", null, { resultUnknown: lostResultUnknown(options), correlationId, request, timedOut, answerBegan });
}

/** Without this a request nothing answers would leave its page loading forever. */
async function once<T>(url: string, options: RequestOptions, correlationId: string): Promise<Tagged<T>> {
  const limit = new AbortController();
  const timer = setTimeout(() => limit.abort(new DOMException("No answer in time", "TimeoutError")), REQUEST_LIMIT_MS);
  const signal = options.signal ? AbortSignal.any([options.signal, limit.signal]) : limit.signal;
  try {
    return await exchange<T>(url, { ...options, signal }, correlationId);
  } finally {
    clearTimeout(timer);
  }
}

async function exchange<T>(url: string, options: RequestOptions, correlationId: string): Promise<Tagged<T>> {
  const request = { method: options.method ?? "GET", url };
  let response: Response;
  try {
    response = await fetch(url, {
      method: options.method ?? "GET",
      headers: requestHeaders(options, correlationId),
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal,
      // A cached reply can be a page the web server sent for this path, and the request never leaves the browser.
      cache: "no-store",
    });
  } catch (cause) {
    throw noAnswer(cause, options, request, correlationId, false);
  }

  if (!response.ok) {
    if (response.status === 401 && !options.answers?.includes(401)) onUnauthorized();
    throw await refusal(response, options, request, correlationId);
  }

  try {
    const text = await response.text();
    return { data: (text ? JSON.parse(text) : undefined) as T, etag: response.headers.get("ETag") };
  } catch (cause) {
    throw noAnswer(cause, options, request, correlationId, true);
  }
}

/** Past `send` on purpose: a report that failed must not become a report, and a lost one is lost. */
export function postBrowserReport(report: BrowserReport): void {
  const headers = requestHeaders({ body: report }, newCorrelationId());
  void fetch(`${API_BASE}/browser-reports`, { method: "POST", headers, body: JSON.stringify(report), keepalive: true }).catch(() => undefined);
}

function api<T>(path: string, options?: RequestOptions): Promise<Tagged<T>> {
  return send<T>(`${API_BASE}${path}`, options);
}

function batchPath(farmId: string, id: string): string {
  return `/batches/${encodeURIComponent(farmId)}/${encodeURIComponent(id)}`;
}

export const client = {
  devLogin: (user: string) => api<Me>("/auth/dev-login", { method: "POST", body: { user }, safeToRepeat: true }),
  logout: () => api<unknown>("/auth/logout", { method: "POST", safeToRepeat: true }),
  me: (signal?: AbortSignal) => api<Me>("/me", { signal, answers: [429] }),
  /** Answers 200 signed in or not: a 401 here would log a console error on every signed-out load. */
  restoreSession: () => api<{ me: Me | null }>("/session", { answers: [429] }),

  listBatches: (query: BatchQuery, signal?: AbortSignal) => api<Batch[]>(`/batches${batchQuery(query)}`, { signal }),
  createBatch: (farmId: string, product: string) => api<Batch>("/batches", { method: "POST", body: { farmId, product } }),
  getBatch: (farmId: string, id: string, signal?: AbortSignal) => api<Batch>(batchPath(farmId, id), { signal }),
  advance: (farmId: string, id: string, to: BatchState, ifMatch: string) =>
    api<Batch>(`${batchPath(farmId, id)}/advance`, { method: "POST", body: { to }, ifMatch }),
  inspect: (farmId: string, id: string, passed: boolean, reason: string | null, ifMatch: string) =>
    api<Batch>(`${batchPath(farmId, id)}/inspect`, { method: "POST", body: { passed, reason }, ifMatch }),
  failNext: (farmId: string, id: string) => api<Batch>(`${batchPath(farmId, id)}/fail-next`, { method: "POST" }),
  replay: (farmId: string, id: string) => api<Batch>(`${batchPath(farmId, id)}/replay`, { method: "POST" }),

  runCompliance: () => api<ComplianceResult>("/compliance/run", { method: "POST" }),

  // A 503 here is the Monitor saying Prometheus is down, so retrying only delays saying so.
  metricsSeries: (bucket: Bucket, signal?: AbortSignal) => send<MetricsReply>(`/metrics/series?bucket=${bucket}`, { signal, answers: [503] }),
  lineLogs: (filters: LogFilters, signal?: AbortSignal) => send<LineLogs>(`${LINE_URL}/line/logs?${logQuery(filters, LOG_LIMIT)}`, { signal }),
  serviceLogs: (filters: LogFilters, signal?: AbortSignal) => send<ServiceLogs>(`${LIVE_URL}/live/logs?${logQuery(filters, LOG_LIMIT)}`, { signal }),
  ingressLogs: (correlationId: string, signal?: AbortSignal) => send<IngressLogs>(`/ingress/logs?correlationId=${encodeURIComponent(correlationId)}`, { signal, answers: [503] }),
  lineStart: (rate: number) => send<unknown>(`${LINE_URL}/line`, { method: "POST", body: { rate } }),
  lineStop: () => send<unknown>(`${LINE_URL}/line`, { method: "DELETE" }),
};
