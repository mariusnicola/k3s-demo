import type { LineStatus } from "../../../api/types";
import { LINE_RATE_MAX, LINE_RATE_MIN, LINE_RATE_STEP } from "../../../config";
import { formatCount, formatTime } from "../../../logic/format";
import { LINE_ERRORS_PATH } from "../../../logic/monitor/logs";
import { AppLink, Button, Cluster, KeyValues, Lever, StatusBadge, Text } from "../../../ds";
import type { LineControl } from "./useLineControl";

export function LineState({ line, status }: { line: LineControl; status: LineStatus }) {
  const { frame, lost, sent, running, producing } = line;
  return (
    <Cluster gap={2}>
      {lost ? (
        <StatusBadge tone="neutral" icon="clock">
          {sent ? `${sent === "stop" ? "Stop" : "Start"} accepted. The state is unknown until the stream returns.` : `Last known: ${running ? `running at ${status.rate} per second` : "stopped"}. Retrying.`}
        </StatusBadge>
      ) : running && !producing ? (
        <StatusBadge tone="neutral" icon="stop">
          Idle: rate is 0
        </StatusBadge>
      ) : running ? (
        <StatusBadge tone="info" icon="play">
          Running, {status.rate} per second requested
        </StatusBadge>
      ) : (
        <StatusBadge tone="neutral" icon="stop">
          Stopped
        </StatusBadge>
      )}
      <Text size="xs" tone="secondary">
        As of {formatTime(frame?.at ?? null)}
      </Text>
    </Cluster>
  );
}

export function LineCounts({ status, achieved }: { status: LineStatus; achieved: number | null }) {
  return (
    <KeyValues
      figures
      items={[
        { label: "Started", value: formatCount(status.started) },
        { label: "Closed", value: formatCount(status.closed) },
        { label: "Rejected", value: formatCount(status.rejected) },
        { label: "Started per second", value: achieved === null ? "Measuring" : achieved.toFixed(1) },
        { label: "Slots in use", value: formatCount(status.inFlight) },
        { label: "Throttled", value: formatCount(status.throttled) },
        {
          label: "Walks dropped",
          value: (
            <AppLink to={LINE_ERRORS_PATH} label={`${formatCount(status.errors)} walks dropped, open the logs`}>
              {status.errors > 0 ? <Text tone="danger">{formatCount(status.errors)}</Text> : "0"}
            </AppLink>
          ),
        },
      ]}
    />
  );
}

const rateWords = (rate: number) => `${rate} ${rate === 1 ? "batch" : "batches"} per second`;

function RateLever({ line, status }: { line: LineControl; status: LineStatus }) {
  const { rate, rateFailed, moveRate, releaseRate, action, running, stopping } = line;
  return (
    <Lever
      label="Requested rate"
      help={running ? "The new rate goes to the line when you let go." : "The line starts at this rate."}
      valueText={rateWords(rate)}
      value={rate}
      min={LINE_RATE_MIN}
      max={LINE_RATE_MAX}
      step={LINE_RATE_STEP}
      major={5}
      minor={1}
      disabled={action.busy && stopping}
      error={rateFailed ? `The new rate was not confirmed. The lever is back at the rate the line reports, ${status.rate} per second.` : null}
      onMove={moveRate}
      onRelease={releaseRate}
    />
  );
}

export function LineRateForm({ line, status }: { line: LineControl; status: LineStatus }) {
  const { action, running, lost, stopping, submit, stop } = line;
  return (
    <>
      <RateLever line={line} status={status} />
      <Cluster gap={2} align="start">
        {running ? null : (
          <Button variant="primary" icon="play" loading={action.busy && !stopping} disabled={action.busy && stopping} onClick={submit}>
            Start line
          </Button>
        )}
        <Button icon="stop" loading={action.busy && stopping} disabled={action.busy && !stopping} disabledReason={running || lost ? null : "The line is not running."} onClick={stop}>
          Stop line
        </Button>
      </Cluster>
    </>
  );
}
