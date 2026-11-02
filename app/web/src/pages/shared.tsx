import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { ApiError, describeProblem, nextStep, serverWords } from "../api/problem";
import { useSession } from "../auth/session";
import { useHopState } from "../live/hopFeed";
import { REQUEST_LIMIT_MS } from "../config";
import { can } from "../logic/batch/batchRules";
import { Banner, Button, Dialog, ErrorDetail, ErrorState, KeyValues, Text, type ErrorExplanation } from "../ds";

export interface ProblemHandlers {
  /** Reads the batch again. */
  onReload?: () => void;
  /** Repeats the failed read. A returned promise keeps the button busy, so a second failure is not a dead press. */
  onRetry?: () => unknown;
  /** Clears a failed write; the operator repeats it with the same control. */
  onDismiss?: () => void;
}

function BackToBatches() {
  const navigate = useNavigate();
  return (
    <Button variant="primary" icon="arrowLeft" onClick={() => navigate("/batches")}>
      Back to batches
    </Button>
  );
}

function refreshButton(label: string, onClick: () => void, loading?: boolean) {
  return (
    <Button variant="primary" icon="refresh" loading={loading} onClick={onClick}>
      {label}
    </Button>
  );
}

/** Every refusal ends in something the operator can press, and each label says what the press really does. */
function RecoveryButtons({ error, onReload, onRetry, onDismiss }: { error: unknown } & ProblemHandlers) {
  const view = describeProblem(error);
  const { signOut } = useSession();
  const [retrying, setRetrying] = useState(false);
  const retry = () => {
    setRetrying(true);
    void Promise.resolve(onRetry?.()).finally(() => setRetrying(false));
  };
  const dismiss = onDismiss ? <Button onClick={onDismiss}>Dismiss</Button> : null;
  switch (view.recovery) {
    case "reload-batch":
      return onReload ? refreshButton("Reload batch", onReload) : (dismiss ?? <BackToBatches />);
    case "retry":
      return onRetry ? refreshButton("Try again", retry, retrying) : (dismiss ?? <BackToBatches />);
    case "sign-in":
      return (
        <Button variant="primary" onClick={signOut}>
          Go to sign in
        </Button>
      );
    case "back-to-batches":
      return <BackToBatches />;
    default:
      return dismiss ?? <BackToBatches />;
  }
}

/** The Logs page redirects anyone without ops.admin to their dashboard, so for them a link would not lead to Logs. */
export function useCanOpenLogs(): boolean {
  const { me } = useSession();
  return Boolean(me) && can(me, "ops.admin");
}

/** The session's own sentence stays, because only the page knows what this means for the user. The server's words fill in the rest. */
export function sessionProblem(what: string, next: string, error: unknown): ErrorExplanation {
  const said = serverWords(error);
  return { what, where: said.where, impact: said.impact, next: nextStep(error, next), correlationId: describeProblem(error).correlationId, reportId: describeProblem(error).reportId };
}

function explainProblem(error: unknown): ErrorExplanation {
  const view = describeProblem(error);
  const technical = error instanceof ApiError && error.status > 0 && !error.local ? <KeyValues items={[{ label: "Server answer", value: `${error.status} ${error.title}` }]} /> : undefined;
  return { what: view.message, where: view.where, impact: view.impact, next: view.next, correlationId: view.correlationId, reportId: view.reportId, technical };
}

export function ProblemBanner({ error, ...handlers }: { error: unknown } & ProblemHandlers) {
  const view = describeProblem(error);
  const logsLink = useCanOpenLogs();
  return (
    <Banner tone="danger" title={view.title} live="assertive" actions={<RecoveryButtons error={error} {...handlers} />}>
      <ErrorDetail explanation={explainProblem(error)} logsLink={logsLink} />
    </Banner>
  );
}

const TOO_MANY_OPEN = "Too many batches are open for the live service to hold them all";

/** Beside the numbers themselves, because only those three and the alerts depend on the rows the scan kept. */
export function ShortCounts() {
  const { state } = useHopState();
  return state.capped ? (
    <Text size="xs" tone="secondary" as="p">
      {TOO_MANY_OPEN}, so Event in flight, Blocked and Flagged may read low.
    </Text>
  ) : null;
}

export const SHORT_ALERTS = `${TOO_MANY_OPEN}, so this list may be missing alerts.`;

/** For a page that read once: live updates are back, but what they missed is not. */
export function MissedBanner({ onRead }: { onRead: () => unknown }) {
  return (
    <Banner floating="page" tone="warning" title="Live updates were lost" live="off" actions={<Button onClick={() => void onRead()}>Read again</Button>}>
      Changes made during the gap may be missing here.
    </Banner>
  );
}

export function ProblemState({ error, ...handlers }: { error: unknown } & ProblemHandlers) {
  const view = describeProblem(error);
  const logsLink = useCanOpenLogs();
  return (
    <ErrorState title={view.title} action={<RecoveryButtons error={error} {...handlers} />}>
      <ErrorDetail explanation={explainProblem(error)} logsLink={logsLink} />
    </ErrorState>
  );
}

/** Cancel and Close are locked while a write is in flight, so the dialog says why and for how long. */
export function BusyNote({ busy }: { busy: boolean }) {
  return busy ? (
    <Text size="xs" tone="secondary" as="p">
      Cancel is locked while this runs. It opens when the server answers, or after {REQUEST_LIMIT_MS / 1000} seconds.
    </Text>
  ) : null;
}

export interface ConfirmDialogProps {
  open: boolean;
  title: string;
  body: string;
  confirm: string;
  busy: boolean;
  danger?: boolean;
  onClose: () => void;
  onConfirm: () => void;
}

export function ConfirmDialog({ open, title, body, confirm, busy, danger, onClose, onConfirm }: ConfirmDialogProps) {
  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) onClose();
      }}
      title={title}
      description={body}
      footer={
        <>
          <Button disabled={busy} onClick={onClose}>
            Cancel
          </Button>
          <Button variant={danger ? "danger" : "primary"} loading={busy} onClick={onConfirm}>
            {confirm}
          </Button>
        </>
      }
    >
      <BusyNote busy={busy} />
    </Dialog>
  );
}

export const FAIL_NEXT_CONFIRM = {
  title: "Fail the next delivery",
  body: "The next event of this batch goes to dead letters and the batch becomes Blocked until an admin replays it.",
  confirm: "Fail next delivery",
};
