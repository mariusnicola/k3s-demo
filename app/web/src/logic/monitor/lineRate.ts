export const RATE_WINDOW_MS = 60_000;
/** Starts come in bursts, so a shorter span would make the number jump. */
const MIN_SPAN_MS = 5_000;

export interface RateSample {
  at: string;
  started: number;
}

export function keepSamples(samples: readonly RateSample[], next: RateSample): RateSample[] {
  const newest = Date.parse(next.at);
  const last = samples.at(-1);
  if (last && next.started < last.started) return [next];
  return [...samples.filter((s) => newest - Date.parse(s.at) <= RATE_WINDOW_MS), next];
}

export function achievedRate(samples: readonly RateSample[]): number | null {
  const first = samples[0];
  const last = samples.at(-1);
  if (!first || !last) return null;
  const span = Date.parse(last.at) - Date.parse(first.at);
  return span < MIN_SPAN_MS ? null : ((last.started - first.started) * 1000) / span;
}
