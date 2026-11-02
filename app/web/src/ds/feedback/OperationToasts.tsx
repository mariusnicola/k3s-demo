import type { ReactNode } from "react";
import type { StepMark, StepView } from "../../logic/monitor/operations";
import { Icon } from "../Icon";
import { ErrorDetail, type ErrorExplanation } from "./ErrorDetail";
import { TONE_ICON, type Tone } from "./Feedback";

export interface OperationItem {
  id: string;
  state: "running" | "no-signal" | "done" | "failed";
  title: string;
  detail: string | null;
  elapsed: string | null;
  onSeen?: () => void;
  explanation?: ErrorExplanation;
  logsLink?: boolean;
  /** The operation itself, where the title adds a word such as failed. */
  name?: string;
  steps?: StepView[];
  /** Null once there is nothing left to estimate. */
  eta?: string | null;
}

const STEP_MARK: Record<StepMark, ReactNode> = {
  done: <Icon name="check" />,
  running: <span className="ds-spinner" />,
  pending: null,
  failed: <Icon name="close" />,
  unknown: <Icon name="warning" />,
};

function Checklist({ label, steps }: { label: string; steps: StepView[] }) {
  // Off inside the live region: the running clock changes every second, and only a tick is news.
  return (
    <ol className="ds-checklist" aria-label={label} aria-live="off" tabIndex={0}>
      {steps.map((step, index) => (
        <li key={index} data-mark={step.mark}>
          <span className="ds-step-mark" aria-hidden="true">
            {STEP_MARK[step.mark]}
          </span>
          <span className="ds-step-name">{step.name}</span>
          <span className="ds-step-note">{step.note}</span>
        </li>
      ))}
    </ol>
  );
}

const OPERATION_TONE: Record<OperationItem["state"], Tone> = { running: "info", "no-signal": "warning", done: "success", failed: "danger" };

function OperationMark({ item }: { item: OperationItem }) {
  if (item.state !== "running") return <Icon name={TONE_ICON[OPERATION_TONE[item.state]]} />;
  if (item.steps) return null;
  return (
    <>
      <span className="ds-spinner" aria-hidden="true" />
      <span className="ds-sr-only">Running</span>
    </>
  );
}

function OperationBody({ item }: { item: OperationItem }) {
  return (
    <div className="ds-operation-body">
      <p className="ds-operation-title">{item.title}</p>
      {item.detail ? <p className="ds-operation-detail">{item.detail}</p> : null}
      {item.steps ? <Checklist label={`Steps of ${item.name ?? item.title}`} steps={item.steps} /> : null}
      {item.eta ? (
        <p className="ds-operation-detail" aria-live="off">
          {item.eta}
        </p>
      ) : null}
      {item.explanation ? (
        <details className="ds-operation-more">
          <summary>Details</summary>
          <ErrorDetail explanation={item.explanation} logsLink={item.logsLink} />
        </details>
      ) : null}
    </div>
  );
}

function Operation({ item }: { item: OperationItem }) {
  return (
    <div className="ds-operation" data-tone={OPERATION_TONE[item.state]}>
      <OperationMark item={item} />
      <OperationBody item={item} />
      {item.elapsed ? (
        <span className="ds-operation-elapsed" aria-live="off">
          {item.elapsed}
        </span>
      ) : null}
      {item.onSeen ? (
        <button type="button" className="ds-operation-seen" aria-label={`Seen: ${item.name ?? item.title}`} onClick={item.onSeen}>
          Seen
        </button>
      ) : null}
    </div>
  );
}

/** Floats top right over the page and never moves it. Failure details stay folded, so a failure stays small. */
export function OperationToasts({ items, inline = false, announcement = "" }: { items: OperationItem[]; inline?: boolean; announcement?: string }) {
  return (
    <div className="ds-operations" data-inline={inline || undefined} role="status" aria-atomic="false">
      <p className="ds-sr-only ds-operations-announce">{announcement}</p>
      {items.map((item) => (
        <Operation key={item.id} item={item} />
      ))}
    </div>
  );
}
