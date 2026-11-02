export interface ChartPoint {
  at: string;
  value: number | null;
}

export interface ChartSeries {
  label: string;
  points: ChartPoint[];
}

export const HEIGHT = 180;
export const LEFT = 44;
export const RIGHT = 12;
export const TOP = 8;
const BOTTOM = 22;
export const GRID_LINES = 3;

export interface Plot {
  width: number;
  count: number;
  max: number;
  plotWidth: number;
  plotHeight: number;
  x: (index: number) => number;
  y: (value: number) => number;
  at: (index: number) => string;
}

/** Works below 1 too, so milliseconds shown in seconds still fill the chart. Nothing above zero keeps a plain 0 to 1. */
function niceMax(values: number[]): number {
  const max = Math.max(0, ...values);
  if (max === 0) return 1;
  const power = 10 ** Math.floor(Math.log10(max));
  const step = [1, 2, 5, 10].find((s) => s * power >= max)!;
  return step * power;
}

/** Runs of known values: a null ends a line, so an unknown bucket is never drawn as a slope or a zero. */
export function runs(points: ChartPoint[]): { index: number; value: number }[][] {
  const out: { index: number; value: number }[][] = [];
  let run: { index: number; value: number }[] = [];
  points.forEach((point, index) => {
    if (point.value === null) {
      if (run.length > 0) out.push(run);
      run = [];
    } else run.push({ index, value: point.value });
  });
  if (run.length > 0) out.push(run);
  return out;
}

/** Puts every series on one list of times, so stacked charts line up. A time a series lacks is unknown. */
export function alignTo(series: ChartSeries[], buckets: string[]): ChartSeries[] {
  return series.map((s) => {
    const byTime = new Map(s.points.map((p) => [p.at, p.value]));
    return { ...s, points: buckets.map((at) => ({ at, value: byTime.get(at) ?? null })) };
  });
}

export function buildPlot(series: ChartSeries[], width: number, buckets?: string[]): Plot {
  const count = buckets?.length ?? series[0]?.points.length ?? 0;
  const known = series.flatMap((s) => s.points.flatMap((p) => (p.value === null ? [] : [p.value])));
  const max = niceMax(known);
  const plotWidth = width - LEFT - RIGHT;
  const plotHeight = HEIGHT - TOP - BOTTOM;
  return {
    width,
    count,
    max,
    plotWidth,
    plotHeight,
    x: (index) => LEFT + (count > 1 ? (index / (count - 1)) * plotWidth : plotWidth / 2),
    y: (value) => TOP + plotHeight - (value / max) * plotHeight,
    at: (index) => buckets?.[index] ?? series[0]?.points[index]?.at ?? "",
  };
}
