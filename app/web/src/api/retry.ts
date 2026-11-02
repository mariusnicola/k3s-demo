import { MAX_ATTEMPTS, MAX_DELAY_MS, backoffMs } from "../logic/backoff";
import { ApiError, STATUS_NETWORK } from "./problem";

const RETRIED = new Set([429, 502, 503, 504, STATUS_NETWORK]);

export interface RetryNotice {
  attempt: number;
  /** Set when the server named the wait. */
  nextInMs: number | null;
}

const retrying = new Map<symbol, RetryNotice>();
const retryListeners = new Set<() => void>();
let retryNotice: RetryNotice | null = null;

function publishRetry(): void {
  retryNotice = [...retrying.values()].reduce<RetryNotice | null>((top, notice) => (!top || notice.attempt > top.attempt ? notice : top), null);
  retryListeners.forEach((listener) => listener());
}

export function subscribeRetries(listener: () => void): () => void {
  retryListeners.add(listener);
  return () => {
    retryListeners.delete(listener);
  };
}

export function readRetryNotice(): RetryNotice | null {
  return retryNotice;
}

export function noteRetry(id: symbol, notice: RetryNotice): void {
  retrying.set(id, notice);
  publishRetry();
}

export function clearRetry(id: symbol): void {
  if (retrying.delete(id)) publishRetry();
}

function waitFor(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const aborted = () => reject(new DOMException("Aborted", "AbortError"));
    if (signal?.aborted) {
      aborted();
      return;
    }
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(timer);
      aborted();
    };
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

let wait: (ms: number, signal?: AbortSignal) => Promise<void> = waitFor;

/** Test seam: the real path runs, only the time passing is swapped. */
export function setRetryWait(next: ((ms: number, signal?: AbortSignal) => Promise<void>) | undefined): void {
  wait = next ?? waitFor;
}

export function retryWait(ms: number, signal?: AbortSignal): Promise<void> {
  return wait(ms, signal);
}

/** Null means give up. A server asking for longer than the longest delay is told no more for now. */
export function nextWait(error: unknown, attempt: number): number | null {
  // A retry would start the whole limit again, so one silent server would hold the page for many limits.
  if (!(error instanceof ApiError) || !RETRIED.has(error.status) || error.timedOut || attempt >= MAX_ATTEMPTS) return null;
  if (error.retryAfterMs !== null && error.retryAfterMs > MAX_DELAY_MS) return null;
  return Math.max(error.retryAfterMs ?? 0, backoffMs(attempt));
}
