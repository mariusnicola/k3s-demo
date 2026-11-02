import { useEffect, useId, useRef, useState, type KeyboardEvent, type PointerEvent, type RefObject } from "react";
import { formatCount } from "../../logic/format";
import { Button } from "../controls/Button";
import { DataTable, type Column } from "../data/DataTable";
import { GRID_LINES, HEIGHT, LEFT, RIGHT, TOP, alignTo, buildPlot, runs, type ChartSeries, type Plot } from "./plot";

type FormatAt = (at: string) => string;
type FormatValue = (value: number) => string;

const wholeCount: FormatValue = (value) => formatCount(Math.round(value));

function valueText(value: number | null | undefined, formatValue: FormatValue): string {
  return value === null || value === undefined ? "Unknown" : formatValue(value);
}

function useBoxWidth(box: RefObject<HTMLDivElement | null>) {
  const [width, setWidth] = useState(640);
  useEffect(() => {
    const element = box.current;
    if (!element) return;
    const observer = new ResizeObserver(([entry]) => {
      if (entry && entry.contentRect.width > 0) setWidth(entry.contentRect.width);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  return width;
}

function useActiveBucket({ count, plotWidth }: Plot) {
  const [active, setActive] = useState<number | null>(null);
  const onMove = (event: PointerEvent<SVGSVGElement>) => {
    const rect = event.currentTarget.getBoundingClientRect();
    const share = (event.clientX - rect.left - LEFT) / Math.max(1, plotWidth);
    setActive(Math.min(count - 1, Math.max(0, Math.round(share * (count - 1)))));
  };
  const onKey = (event: KeyboardEvent<SVGSVGElement>) => {
    const next: Record<string, number> = { ArrowLeft: (active ?? count) - 1, ArrowRight: (active ?? -1) + 1, Home: 0, End: count - 1 };
    const target = next[event.key];
    if (target === undefined) return;
    event.preventDefault();
    setActive(Math.min(count - 1, Math.max(0, target)));
  };
  const clear = () => setActive(null);
  return { active, events: { onPointerMove: onMove, onPointerLeave: clear, onBlur: clear, onKeyDown: onKey } };
}

function ChartLegend({ caption, series }: { caption: string; series: ChartSeries[] }) {
  if (series.length <= 1) return null;
  return (
    <ul className="ds-chart-legend" aria-label={`${caption} legend`}>
      {series.map((s, i) => (
        <li key={s.label}>
          <span className="ds-chart-key" data-series={i} aria-hidden="true" />
          {s.label}
        </li>
      ))}
    </ul>
  );
}

function GridLines({ plot: { max, width, y }, formatValue }: { plot: Plot; formatValue: FormatValue }) {
  return Array.from({ length: GRID_LINES + 1 }, (_, i) => {
    const value = (max / GRID_LINES) * i;
    return (
      <g key={i}>
        <line className="ds-chart-grid" x1={LEFT} x2={width - RIGHT} y1={y(value)} y2={y(value)} />
        <text className="ds-chart-axis" x={LEFT - 6} y={y(value) + 4} textAnchor="end">
          {formatValue(value)}
        </text>
      </g>
    );
  });
}

function TimeRange({ plot: { count, width, at }, formatAt }: { plot: Plot; formatAt: FormatAt }) {
  if (count === 0) return null;
  return (
    <>
      <text className="ds-chart-axis" x={LEFT} y={HEIGHT - 6}>
        {formatAt(at(0))}
      </text>
      <text className="ds-chart-axis" x={width - RIGHT} y={HEIGHT - 6} textAnchor="end">
        {formatAt(at(count - 1))}
      </text>
    </>
  );
}

function SeriesLines({ series, plot: { x, y } }: { series: ChartSeries[]; plot: Plot }) {
  return series.map((s, i) =>
    runs(s.points).map((run) =>
      run.length === 1 ? (
        <circle key={run[0]!.index} data-series={i} className="ds-chart-dot" cx={x(run[0]!.index)} cy={y(run[0]!.value)} r={4} />
      ) : (
        <path key={run[0]!.index} data-series={i} className="ds-chart-line" d={run.map((p, n) => `${n === 0 ? "M" : "L"}${x(p.index)} ${y(p.value)}`).join(" ")} />
      ),
    ),
  );
}

function ActiveDots({ series, active, plot: { x, y } }: { series: ChartSeries[]; active: number; plot: Plot }) {
  return series.map((s, i) => {
    const value = s.points[active]?.value;
    return value === null || value === undefined ? null : <circle key={s.label} data-series={i} className="ds-chart-dot" data-active cx={x(active)} cy={y(value)} r={4} />;
  });
}

function ChartTooltip({ id, series, active, plot: { x, width, at }, formatAt, formatValue }: { id: string; series: ChartSeries[]; active: number; plot: Plot; formatAt: FormatAt; formatValue: FormatValue }) {
  return (
    <div id={id} className="ds-chart-tooltip" role="tooltip" style={{ left: Math.min(x(active), width - 160) }}>
      <p>{formatAt(at(active))}</p>
      {series.map((s, i) => (
        <p key={s.label}>
          <span className="ds-chart-key" data-series={i} aria-hidden="true" />
          <strong>{valueText(s.points[active]?.value, formatValue)}</strong> {s.label}
        </p>
      ))}
    </div>
  );
}

function ChartValues({ caption, series, plot: { count, at }, formatAt, formatValue }: { caption: string; series: ChartSeries[]; plot: Plot; formatAt: FormatAt; formatValue: FormatValue }) {
  const [table, setTable] = useState(false);
  const columns: Column<number>[] = [
    { key: "at", header: "Bucket", render: (i) => formatAt(at(i)) },
    ...series.map((s) => ({ key: s.label, header: s.label, render: (i: number) => valueText(s.points[i]?.value, formatValue) })),
  ];
  return (
    <>
      <Button variant="ghost" aria-label={`${table ? "Hide" : "Show"} values, ${caption}`} onClick={() => setTable((on) => !on)}>
        {table ? "Hide values" : "Show values"}
      </Button>
      {table ? <DataTable caption={`${caption} values`} columns={columns} rows={Array.from({ length: count }, (_, i) => i)} rowKey={(i) => at(i)} /> : null}
    </>
  );
}

interface SvgProps {
  caption: string;
  series: ChartSeries[];
  plot: Plot;
  active: number | null;
  describedBy: string | undefined;
  events: ReturnType<typeof useActiveBucket>["events"];
  formatAt: FormatAt;
  formatValue: FormatValue;
}

function ChartSvg({ caption, series, plot, active, describedBy, events, formatAt, formatValue }: SvgProps) {
  return (
    <svg
      role="img"
      aria-label={`${caption}, ${plot.count} buckets. Press Home, End or the arrow keys to read each bucket, or show the values as a table.`}
      tabIndex={0}
      aria-describedby={describedBy}
      width={plot.width}
      height={HEIGHT}
      {...events}
    >
      <GridLines plot={plot} formatValue={formatValue} />
      <TimeRange plot={plot} formatAt={formatAt} />
      {active !== null ? <line className="ds-chart-crosshair" x1={plot.x(active)} x2={plot.x(active)} y1={TOP} y2={TOP + plot.plotHeight} /> : null}
      <SeriesLines series={series} plot={plot} />
      {active !== null ? <ActiveDots series={series} active={active} plot={plot} /> : null}
    </svg>
  );
}

interface LineChartProps {
  caption: string;
  series: ChartSeries[];
  formatAt: FormatAt;
  /** For the axis, the tooltip and the values table. Whole counts without it. */
  formatValue?: FormatValue;
  /** One list of times for every chart in a stack, so a moment sits at one place in all of them. */
  buckets?: string[];
}

/** Time series on one axis. Colours come from the --series tokens, validated for both themes; at most four series. */
export function LineChart({ caption, series: given, formatAt, formatValue = wholeCount, buckets }: LineChartProps) {
  const box = useRef<HTMLDivElement>(null);
  const series = buckets ? alignTo(given, buckets) : given;
  const plot = buildPlot(series, useBoxWidth(box), buckets);
  const { active, events } = useActiveBucket(plot);
  const tooltipId = useId();
  return (
    <div className="ds-chart">
      <ChartLegend caption={caption} series={series} />
      <div className="ds-chart-plot" ref={box}>
        <ChartSvg caption={caption} series={series} plot={plot} active={active} describedBy={active !== null ? tooltipId : undefined} events={events} formatAt={formatAt} formatValue={formatValue} />
        {active !== null ? <ChartTooltip id={tooltipId} series={series} active={active} plot={plot} formatAt={formatAt} formatValue={formatValue} /> : null}
      </div>
      <ChartValues caption={caption} series={series} plot={plot} formatAt={formatAt} formatValue={formatValue} />
    </div>
  );
}
