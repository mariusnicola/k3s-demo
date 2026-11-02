import { LINE_URL } from "../../../config";
import { Banner, ConnectionStatus, ErrorDetail, Panel, Skeleton } from "../../../ds";
import { ProblemBanner } from "../../shared";
import { LineCounts, LineRateForm, LineState } from "./LineView";
import { useLineControl } from "./useLineControl";

export function LinePanel({ now }: { now: number }) {
  const line = useLineControl();
  const { feed, status, lost, action } = line;

  return (
    <Panel
      title="Production line"
      description="Creates batches and walks them to Closed through the API. One in ten fails inspection."
      actions={<ConnectionStatus label="Line updates" state={feed.state} now={now} onReconnect={feed.reconnect} />}
    >
      {lost && !status ? (
        <Banner tone="danger" title="Production line state unknown" live="assertive">
          <ErrorDetail explanation={{ what: `No state came from the line service at ${LINE_URL}. Its state is unknown.`, where: "Production line", next: "It reconnects by itself. If this stays, check the line service." }} />
        </Banner>
      ) : !status ? (
        <Skeleton rows={2} label="Reading production line state" />
      ) : (
        <>
          <LineState line={line} status={status} />
          <LineCounts status={status} achieved={line.achieved} />
          <LineRateForm line={line} status={status} />
        </>
      )}
      {action.error ? <ProblemBanner error={action.error} onDismiss={action.clearError} /> : null}
    </Panel>
  );
}
