import { REQUEST_LIMIT_MS } from "../config";
import { reportThrown } from "./reports";
import { requestPlace, type RequestPlace } from "./requestPlace";

export interface ApiErrorExtras {
  /** A write with no answer: it may have landed. */
  resultUnknown?: boolean;
  retryAfterMs?: number | null;
  correlationId?: string | null;
  request?: ProblemRequest;
  /** Refused in the browser before anything was sent. */
  local?: boolean;
  said?: ServerWords;
  /** Nothing answered within the limit. */
  timedOut?: boolean;
  /** Headers arrived, so something answered, whatever became of the body. */
  answerBegan?: boolean;
  /** The body gave neither title nor detail, as a proxy's page or a cut body does. */
  noReason?: boolean;
}

/** A problem body's own words. Null where it sent none. */
export interface ServerWords {
  where: string | null;
  impact: string | null;
  next: string | null;
}

const SAID_NOTHING: ServerWords = { where: null, impact: null, next: null };

export interface ProblemRequest {
  method: string;
  url: string;
}

/** Failed request, carrying the RFC 7807 body when the server sent one. */
export class ApiError extends Error {
  readonly resultUnknown: boolean;
  readonly retryAfterMs: number | null;
  readonly correlationId: string | null;
  readonly request: ProblemRequest | null;
  readonly local: boolean;
  readonly said: ServerWords;
  readonly timedOut: boolean;
  readonly answerBegan: boolean;
  readonly noReason: boolean;
  /** Set by the client when it gives up on the request, because only then is it a failure. */
  reportId: string | null = null;

  constructor(
    readonly status: number,
    readonly title: string,
    readonly detail: string | null,
    extras: ApiErrorExtras = {},
  ) {
    super(detail ?? title);
    this.name = "ApiError";
    this.resultUnknown = extras.resultUnknown ?? false;
    this.retryAfterMs = extras.retryAfterMs ?? null;
    this.correlationId = extras.correlationId ?? null;
    this.request = extras.request ?? null;
    this.local = extras.local ?? false;
    this.said = extras.said ?? SAID_NOTHING;
    this.timedOut = extras.timedOut ?? false;
    this.answerBegan = extras.answerBegan ?? false;
    this.noReason = extras.noReason ?? false;
  }
}

export const STATUS_NETWORK = 0;

export type RecoveryAction = "reload-batch" | "retry" | "sign-in" | "back-to-batches" | "fix-input" | "check-state";

export interface ProblemView {
  title: string;
  /** What happened. */
  message: string;
  where: string | null;
  /** What it did to the batch. Null when no request stands behind the error. */
  impact: string | null;
  next: string;
  correlationId: string | null;
  reportId: string | null;
  recovery: RecoveryAction;
}

const NOT_APPLIED = new Set([400, 401, 403, 404, 409, 412, 428, 429]);

function impactOf(status: number, request: ProblemRequest | null, place: RequestPlace | null): string | null {
  if (!request) return null;
  if (request.method === "GET") return "Nothing changed: this was only a read.";
  if (!place?.batch) return "The server did not say what this did to any batch.";
  return NOT_APPLIED.has(status) ? "Nothing was saved." : "This step may not have finished.";
}

type Outcome = Pick<ProblemView, "title" | "message" | "next" | "recovery">;

function waitSeconds(api: ApiError): number {
  return Math.ceil((api.retryAfterMs ?? 0) / 1000);
}

function retryWords(api: ApiError | null): string {
  return api && waitSeconds(api) > 0 ? `Try again in ${waitSeconds(api)} s.` : "Try again in a moment.";
}

/** The header holds the wait, and the server's words need not. A number counts only if it reads as seconds. */
function withWait(next: string | null, api: ApiError | null): string | null {
  if (!next || api?.status !== 429) return next;
  const seconds = waitSeconds(api);
  if (seconds <= 0) return next;
  return new RegExp(`\\b${seconds}\\s*(s|secs?|seconds?)\\b`, "i").test(next) ? next : `${next} ${retryWords(api)}`;
}

const NOTHING_THERE = "Nothing was found at this address.";
const UNREACHABLE: Outcome = { title: "Cannot reach the server", message: "The server did not answer.", next: "Check the connection, then try again.", recovery: "retry" };
const NO_USABLE_ANSWER: Outcome = { title: "No usable answer", message: "The answer was cut off or could not be read.", next: "Try again.", recovery: "retry" };
/** Only a thrown value that is no ApiError lands here, and that came from the page, not from a request. */
const PAGE_FAULT: Outcome = { title: "This page hit an error", message: "The page could not carry this out.", next: "Try again.", recovery: "retry" };

function outcomeOf(status: number, api: ApiError | null, request: ProblemRequest | null): Outcome {
  const detail = api?.detail ?? null;
  const place = request ? requestPlace(request) : null;
  switch (status) {
    case 400:
      return { title: "Check the entered values", message: detail ?? "The server refused the values sent.", next: "Correct the values, then send them again.", recovery: "fix-input" };
    case 401:
      if (place?.where === "Signing in") return { title: "Sign-in refused", message: detail ?? "The server refused this sign-in.", next: "Try again.", recovery: "retry" };
      return { title: "Session ended", message: "Your session ended.", next: "Sign in again to continue.", recovery: "sign-in" };
    case 403:
      return { title: "You do not have access", message: detail ?? "Your user lacks the permission or the farm for this action.", next: "Go back, or ask an admin for access.", recovery: "back-to-batches" };
    case 404:
      if (place?.where === "Signing in") return { title: "Not found", message: detail ?? NOTHING_THERE, next: "Try again.", recovery: "retry" };
      return { title: "Not found", message: detail ?? (place?.one ? "This batch does not exist, or it is on a farm you cannot reach." : NOTHING_THERE), next: "Go back to the batch list.", recovery: "back-to-batches" };
    case 409:
      return { title: "This step is not allowed right now", message: detail ?? "The batch moved on, or an event is still in flight.", next: "Reload the batch to see its current state.", recovery: "reload-batch" };
    case 412:
      return { title: "Someone else changed this batch", message: "The batch changed after you opened it.", next: "Reload the batch, check its state, then repeat the step if it still applies.", recovery: "reload-batch" };
    case 428:
      return { title: "Batch version missing", message: "The request carried no batch version.", next: "Reload the batch, then repeat the step.", recovery: "reload-batch" };
    case 429:
      return { title: "Too many requests", message: detail ?? "The server is limiting requests for now.", next: retryWords(api), recovery: "retry" };
    case STATUS_NETWORK:
      if (!api) return PAGE_FAULT;
      return api.answerBegan || api.timedOut ? NO_USABLE_ANSWER : UNREACHABLE;
    default:
      if (api?.noReason) return { title: "The request failed", message: "The answer gave no reason.", next: nextAfterFailure(request), recovery: "retry" };
      return { title: "The server could not finish this", message: detail || api?.title || "The answer gave no reason.", next: nextAfterFailure(request), recovery: "retry" };
  }
}

function nextAfterFailure(request: ProblemRequest | null): string {
  if (!request || request.method === "GET") return "Try again.";
  return requestPlace(request)?.batch ? "Check the batch before repeating it." : "Check the result before repeating it.";
}

const UNKNOWN_RESULT: Outcome & Pick<ProblemView, "impact"> = {
  title: "The result is unknown",
  message: "No usable answer came back.",
  impact: "This may have saved.",
  next: "Check the current state before repeating it.",
  recovery: "reload-batch",
};

export function serverWords(error: unknown): ServerWords {
  return error instanceof ApiError ? error.said : SAID_NOTHING;
}

/** For a page with its own next step: the server's replaces it, and a 429's wait stays either way. */
export function nextStep(error: unknown, own: string): string {
  return withWait(serverWords(error).next ?? own, error instanceof ApiError ? error : null) ?? own;
}

/** Reported here because this is the one place every failure on a page passes through, and a thrown value has no request to report it. */
function reportIdOf(error: unknown, api: ApiError | null): string | null {
  if (api) return api.reportId;
  return error === null || error === undefined ? null : reportThrown(error);
}

/** One place that turns a status into what the operator reads and can do next. The server's words win; the web's are for when it sent none. */
export function describeProblem(error: unknown): ProblemView {
  const api = error instanceof ApiError ? error : null;
  const status = api ? api.status : STATUS_NETWORK;
  const request = api?.request ?? null;
  const place = request ? requestPlace(request) : null;
  const said = api?.said ?? SAID_NOTHING;
  const common = { where: place?.where ?? null, impact: impactOf(status, request, place), correlationId: api?.correlationId ?? null, reportId: reportIdOf(error, api) };
  const noBatch: Partial<ProblemView> = request && !place?.batch ? { impact: common.impact, recovery: "check-state" } : {};
  const unknown = { ...common, ...UNKNOWN_RESULT, ...noBatch };
  const view = api?.resultUnknown ? unknown : { ...common, ...outcomeOf(status, api, request) };
  if (api?.timedOut) view.message = `No usable answer came back within ${REQUEST_LIMIT_MS / 1000} s.`;
  return { ...view, where: said.where ?? view.where, impact: said.impact ?? view.impact, next: withWait(said.next, api) ?? view.next };
}
