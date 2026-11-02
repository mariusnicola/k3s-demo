import { Component, type ReactNode } from "react";
import { reportThrown } from "../../api/reports";
import { Button } from "../controls/Button";
import { Icon, type IconName } from "../Icon";
import { ReportId } from "./ReportId";

export type Tone = "neutral" | "info" | "success" | "warning" | "danger";

export const TONE_ICON: Record<Tone, IconName> = {
  neutral: "info",
  info: "info",
  success: "check",
  warning: "warning",
  danger: "blocked",
};

/** `floating` keeps a passing notice out of the layout: "panel" over its panel's lower edge, "page" in the corner of the screen. */
export function Banner({ tone = "info", title, children, actions, live = "polite", floating }: { floating?: "panel" | "page"; tone?: Tone; title: string; children?: ReactNode; actions?: ReactNode; live?: "polite" | "assertive" | "off" }) {
  const role = live === "assertive" ? "alert" : live === "polite" ? "status" : undefined;
  return (
    <div className="ds-banner" data-tone={tone} data-floating={floating} role={role}>
      <Icon name={TONE_ICON[tone]} />
      <div className="ds-banner-body">
        <p className="ds-banner-title">{title}</p>
        {children ? <div className="ds-banner-text">{children}</div> : null}
        {actions ? <div className="ds-banner-actions">{actions}</div> : null}
      </div>
    </div>
  );
}

/** A line held open for a note about a live connection, so the note appearing moves nothing. Empty until there is one. */
export function LiveNote({ children }: { children?: ReactNode }) {
  return <p className="ds-live-note">{children}</p>;
}

export function Spinner({ label = "Loading" }: { label?: string }) {
  return (
    <span className="ds-spinner-row" role="status" aria-label={label}>
      <span className="ds-spinner" aria-hidden="true" />
      <span>{label}</span>
    </span>
  );
}

export function Skeleton({ rows = 3, label = "Loading" }: { rows?: number; label?: string }) {
  return (
    <div className="ds-skeleton" role="status" aria-label={label}>
      {Array.from({ length: rows }, (_, i) => (
        <span key={i} className="ds-skeleton-row" />
      ))}
    </div>
  );
}

export function EmptyState({ icon = "box", title, children, action }: { icon?: IconName; title: string; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className="ds-empty">
      <Icon name={icon} />
      <p className="ds-empty-title">{title}</p>
      {children ? <p className="ds-empty-text">{children}</p> : null}
      {action ? <div className="ds-empty-action">{action}</div> : null}
    </div>
  );
}

export function ErrorState({ title, children, action }: { title: string; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className="ds-error-state" role="alert">
      <Icon name="blocked" />
      <p className="ds-empty-title">{title}</p>
      {children ? <div className="ds-empty-text">{children}</div> : null}
      {action ? <div className="ds-empty-action">{action}</div> : null}
    </div>
  );
}

export function CrashState({ onReload, reportId }: { onReload: () => void; reportId?: string | null }) {
  return (
    <ErrorState
      title="This page failed"
      action={
        <Button variant="primary" icon="refresh" onClick={onReload}>
          Reload page
        </Button>
      }
    >
      A change made just before may have been saved. Check the batch list before repeating it. Reload the page, or pick another page from the menu.
      {reportId ? (
        <p>
          Report ID <ReportId id={reportId} />
        </p>
      ) : null}
    </ErrorState>
  );
}

/** A render fault or a failed page chunk must leave a way out, not a blank screen. Give it a key that changes with the page. */
export class ErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean; reportId: string | null }> {
  override state = { failed: false, reportId: null };

  /** Here and not in componentDidCatch: React logs the error between the two, and the log must find it already reported. */
  static getDerivedStateFromError(error: unknown) {
    return { failed: true, reportId: reportThrown(error) };
  }

  override render() {
    return this.state.failed ? <CrashState onReload={() => window.location.reload()} reportId={this.state.reportId} /> : this.props.children;
  }
}
