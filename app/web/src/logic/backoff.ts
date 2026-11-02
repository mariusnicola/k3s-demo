export const MAX_ATTEMPTS = 10;
export const MAX_DELAY_MS = 30_000;
const FIRST_DELAY_MS = 200;

/** Equal jitter: half the delay is fixed, so the attempts still span a restart, and half is random, so tabs spread out. */
export function backoffMs(attempt: number, random: () => number = Math.random): number {
  const delay = Math.min(FIRST_DELAY_MS * 2 ** (attempt - 1), MAX_DELAY_MS);
  return delay / 2 + (random() * delay) / 2;
}

/** Seconds or an HTTP date. A value that is neither is treated as no answer, so the normal delay applies. */
export function retryAfterMs(header: string | null, now: number = Date.now()): number | null {
  if (!header) return null;
  if (/^\d+$/.test(header)) return Number(header) * 1000;
  const date = /GMT$/.test(header) ? Date.parse(header) : Number.NaN;
  return Number.isNaN(date) ? null : Math.max(0, date - now);
}
