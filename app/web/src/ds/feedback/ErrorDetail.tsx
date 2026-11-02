import type { ReactNode } from "react";
import { logsFor } from "../../logic/monitor/logs";
import { AppLink } from "../layout/Layout";
import { CopyButton } from "./CopyButton";
import { ReportId } from "./ReportId";

export interface ErrorExplanation {
  what: ReactNode;
  where?: string | null;
  impact?: string | null;
  next?: string | null;
  correlationId?: string | null;
  reportId?: string | null;
  /** Exception, stack and codes. Folded: most people reading an error never need them. */
  technical?: ReactNode;
}

function ErrorRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="ds-error-row">
      <dt>{label}</dt>
      <dd>{children}</dd>
    </div>
  );
}

function CorrelationRow({ id, logsLink }: { id: string; logsLink?: boolean }) {
  return (
    <ErrorRow label="Correlation ID">
      <span className="ds-correlation">
        <code className="ds-id">{id}</code>
        <CopyButton value={id} label="Copy correlation ID" />
        {logsLink ? (
          <AppLink to={logsFor(id)} label={`Open ${id} in Logs`}>
            Open in Logs
          </AppLink>
        ) : null}
      </span>
    </ErrorRow>
  );
}

/** Every error reads in this order. A row with nothing to say is left out, never shown empty. */
export function ErrorDetail({ explanation, logsLink }: { explanation: ErrorExplanation; logsLink?: boolean }) {
  const { what, where, impact, next, correlationId, reportId, technical } = explanation;
  return (
    <div className="ds-error-block">
      <dl className="ds-error-detail">
        <ErrorRow label="What happened">{what}</ErrorRow>
        {where ? <ErrorRow label="Where">{where}</ErrorRow> : null}
        {impact ? <ErrorRow label="What it did">{impact}</ErrorRow> : null}
        {next ? <ErrorRow label="What to do next">{next}</ErrorRow> : null}
        {correlationId ? <CorrelationRow id={correlationId} logsLink={logsLink} /> : null}
        {reportId ? <ErrorRow label="Report ID"><ReportId id={reportId} /></ErrorRow> : null}
      </dl>
      {technical ? (
        <details className="ds-technical">
          <summary>Technical detail</summary>
          {technical}
        </details>
      ) : null}
    </div>
  );
}
