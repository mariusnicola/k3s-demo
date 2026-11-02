import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { FLOW_STATES, type BatchState, type Step } from "../../api/types";
import { STATE_LABELS } from "../../logic/batch/batchRules";
import { formatCount, formatDateTime } from "../../logic/format";
import type { Tone } from "../feedback/Feedback";
import { Icon, type IconName } from "../Icon";
import { Tooltip } from "../controls/Overlay";

export function StatusBadge({ tone = "neutral", icon, children }: { tone?: Tone; icon?: IconName; children: ReactNode }) {
  return (
    <span className="ds-badge" data-tone={tone}>
      {icon ? <Icon name={icon} /> : null}
      <span>{children}</span>
    </span>
  );
}

const STATE_TONE: Record<BatchState, { tone: Tone; icon: IconName }> = {
  Created: { tone: "neutral", icon: "box" },
  Dose1: { tone: "info", icon: "chevronRight" },
  Dose2: { tone: "info", icon: "chevronRight" },
  Inspected: { tone: "info", icon: "check" },
  Shipped: { tone: "info", icon: "chevronRight" },
  Closed: { tone: "success", icon: "check" },
  Rejected: { tone: "danger", icon: "close" },
};

export function StateBadge({ state }: { state: BatchState }) {
  const { tone, icon } = STATE_TONE[state];
  return (
    <StatusBadge tone={tone} icon={icon}>
      {STATE_LABELS[state]}
    </StatusBadge>
  );
}

/** Six ticks, one per flow step: position shows progress without reading the label. */
export function StateRail({ state }: { state: BatchState }) {
  const rejected = state === "Rejected";
  const reached = rejected ? FLOW_STATES.indexOf("Dose2") : FLOW_STATES.indexOf(state);
  return (
    <span className="ds-rail" role="img" aria-label={rejected ? "Stopped at Dose 2, rejected" : `Step ${reached + 1} of ${FLOW_STATES.length}`}>
      {FLOW_STATES.map((s, i) => (
        <span key={s} className="ds-rail-tick" data-reached={i <= reached || undefined} data-failed={(rejected && i === reached) || undefined} />
      ))}
    </span>
  );
}

/** A null value is unknown, which must not read as a count of zero. `text` is for a figure that is not one count. */
export function Stat({ label, value = null, text, tone, to, detail, hero }: { label: string; value?: number | null; text?: string; tone?: "danger" | "warning"; to?: string; detail?: string; hero?: boolean }) {
  const body = (
    <>
      <span className="ds-stat-value">{text ?? (value === null ? "Unknown" : formatCount(value))}</span>
      <span className="ds-stat-label">{label}</span>
      {detail ? <span className="ds-stat-detail">{detail}</span> : null}
    </>
  );
  const active = value !== null && value > 0 ? tone : undefined;
  return to ? (
    <Link to={to} className="ds-stat" data-tone={active} data-hero={hero || undefined} data-link>
      {body}
    </Link>
  ) : (
    <div className="ds-stat" data-tone={active} data-hero={hero || undefined}>
      {body}
    </div>
  );
}

export interface CountFilterItem {
  value: string;
  title: string;
  detail?: string;
  count: number;
}

/** Counts that double as filters. Pressed state is exposed, so a screen reader hears which one is on. */
export function CountFilterList({ label, items, selected, onSelect }: { label: string; items: CountFilterItem[]; selected: string; onSelect: (value: string) => void }) {
  return (
    <ul className="ds-count-filters" aria-label={label}>
      {items.map((item) => (
        <li key={item.value}>
          <button type="button" className="ds-count-filter" aria-pressed={item.value === selected} onClick={() => onSelect(item.value)}>
            <span className="ds-count-filter-count">{formatCount(item.count)}</span>
            <span className="ds-count-filter-text">
              <span>{item.title}</span>
              {item.detail ? <span className="ds-count-filter-detail">{item.detail}</span> : null}
            </span>
          </button>
        </li>
      ))}
    </ul>
  );
}

/** `reserved` draws a booked amount, not live use: hatched, and the head says what is left or over. */
export function Meter({ label, value, max, unit, breach, stale, reserved }: { label: string; value: number; max: number; unit: string; breach?: boolean; stale?: boolean; reserved?: boolean }) {
  const ratio = max > 0 ? Math.min(1, value / max) : 0;
  const left = reserved ? (value > max ? `, ${formatCount(value - max)} ${unit} over` : `, ${formatCount(max - value)} ${unit} free`) : "";
  return (
    <div className="ds-meter" data-breach={(breach && !stale) || undefined} data-stale={stale || undefined} data-reserved={reserved || undefined}>
      <div className="ds-meter-head">
        <span className="ds-meter-label">{label}</span>
        <span className="ds-meter-value">{`${formatCount(value)} of ${formatCount(max)} ${unit}${left}`}</span>
      </div>
      <div className="ds-meter-track" role="meter" aria-label={label} aria-valuenow={value} aria-valuemin={0} aria-valuemax={max}>
        <span className="ds-meter-fill" data-ratio={Math.round(ratio * 20) * 5} />
      </div>
    </div>
  );
}

/** A tooltip needs a hover or a keyboard. `phoneFull` spells the value out on phones, for values an auditor must read. */
export function IdText({ value, short, phoneFull }: { value: string; short?: string; phoneFull?: boolean }) {
  if (!short || short === value) return <code className="ds-id">{value}</code>;
  return (
    <Tooltip content={value}>
      <code className="ds-id" data-phone-full={phoneFull || undefined} tabIndex={0} aria-label={value}>
        <span className="ds-id-short">{short}</span>
        {phoneFull ? <span className="ds-id-full">{value}</span> : null}
      </code>
    </Tooltip>
  );
}

/** `figures` is for counts: a tile each, the number large above a small label. */
export function KeyValues({ items, figures }: { items: { label: string; value: ReactNode }[]; figures?: boolean }) {
  return (
    <dl className="ds-key-values" data-figures={figures || undefined}>
      {items.map((item) => (
        <div key={item.label} className="ds-key-value">
          <dt>{item.label}</dt>
          <dd>{item.value}</dd>
        </div>
      ))}
    </dl>
  );
}

function hasRecord(step: Step): boolean {
  return Boolean(step.recordId);
}

/** Audit ledger: each step, who did it, and whether its compliance record exists yet. */
export function Timeline({ steps }: { steps: Step[] }) {
  return (
    <ol className="ds-timeline">
      {steps.map((step, i) => (
        <li key={`${step.state}-${i}`} className="ds-timeline-item" data-recorded={hasRecord(step) || undefined} data-rejected={step.state === "Rejected" || undefined}>
          <span className="ds-timeline-marker" aria-hidden="true" />
          <div className="ds-timeline-body">
            <div className="ds-timeline-head">
              <StateBadge state={step.state} />
              <span className="ds-timeline-time">{formatDateTime(step.at)}</span>
            </div>
            <p className="ds-timeline-meta">By {step.by || "unknown"}</p>
            {hasRecord(step) ? (
              <p className="ds-timeline-meta">
                <Icon name="check" />
                <span>Compliance record written</span>
              </p>
            ) : (
              <p className="ds-timeline-meta">
                <Icon name="clock" />
                <span>Compliance record not written yet</span>
              </p>
            )}
          </div>
        </li>
      ))}
    </ol>
  );
}
