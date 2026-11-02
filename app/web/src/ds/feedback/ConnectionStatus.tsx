import type { SocketState } from "../../live/socket";
import { counted, formatAge, formatTime } from "../../logic/format";
import { Button } from "../controls/Button";
import { Icon } from "../Icon";
import { ReportId } from "./ReportId";

export interface ConnectionStatusProps {
  label: string;
  state: SocketState;
  now: number;
  /** An open socket whose data aged out is not healthy either. */
  stale?: boolean;
  onReconnect?: () => void;
}

// Plain functions, not components: every state must reconcile onto the same elements, or the live region is replaced and its change goes unread.
function openView(label: string, last: number | null, drops = 0) {
  return (
    <span className="ds-connection" data-status="open">
      <span className="ds-connection-mark" aria-hidden="true" />
      <span className="ds-connection-label" role="status">
        {label}: live
      </span>
      <span key="detail" className="ds-connection-detail">
        {last === null ? "Connected, no data yet" : `Last data ${formatTime(last)}`}
        {drops > 0 ? `, ${counted(drops, "reset")}` : ""}
      </span>
    </span>
  );
}

function connectingView(label: string) {
  return (
    <span className="ds-connection" data-status="connecting">
      <span className="ds-spinner" aria-hidden="true" />
      <span className="ds-connection-label" role="status">
        {label}: connecting
      </span>
    </span>
  );
}

function faultView({ label, state, now, onReconnect }: ConnectionStatusProps) {
  const last = state.lastDataAt;
  const lastText = last === null ? "No data received" : `Last data ${formatTime(last)}, ${formatAge(now - last)} ago`;
  const word = state.status === "lost" ? "connection lost" : "data stale";
  return (
    <span className="ds-connection" data-status="lost">
      <Icon name="plugOff" />
      <span className="ds-connection-label" role="alert">
        {label}: {word}
      </span>
      <span key="more" className="ds-connection-more">
        <span className="ds-connection-detail">{state.status === "lost" ? `${lastText}. Retrying.` : `${lastText}.`}</span>
        {state.status === "lost" && state.reportId ? (
          <span className="ds-connection-detail">
            Report ID <ReportId id={state.reportId} />
          </span>
        ) : null}
        {onReconnect ? (
          <Button variant="ghost" icon="refresh" onClick={onReconnect}>
            Reconnect now
          </Button>
        ) : null}
      </span>
    </span>
  );
}

/** Never reads live unless the socket is open and fresh. Only the status words are a live region; the ticking age would be read out every second. */
export function ConnectionStatus(props: ConnectionStatusProps) {
  const { label, state, stale } = props;
  if (state.status === "open" && !stale) return openView(label, state.lastDataAt, state.drops);
  if (state.status === "connecting") return connectingView(label);
  return faultView(props);
}
