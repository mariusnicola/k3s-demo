import type { Batch } from "../api/types";
import { eventStatus, isFinal, pendingSince } from "../logic/batch/batchRules";
import { formatDateTime } from "../logic/format";
import { Banner, Button, ErrorDetail, KeyValues } from "../ds";
import { useCanOpenLogs } from "./shared";

interface EventBannerProps {
  batch: Batch;
  now: number;
  lost?: boolean;
  onReplay: () => void;
  replayReason: string | null;
  replaying: boolean;
}

/** A plain function, not a component: every status shares one Banner instance on screen. */
function blockedBanner({ batch, onReplay, replayReason, replaying }: EventBannerProps, pending: NonNullable<Batch["pending"]>, final: boolean, logsLink: boolean) {
  return (
    <Banner
      tone="danger"
      title="Blocked: the compliance event failed"
      live="assertive"
      actions={
        <Button variant="primary" icon="refresh" loading={replaying} disabledReason={replayReason} onClick={onReplay}>
          Replay event
        </Button>
      }
    >
      <ErrorDetail
        logsLink={logsLink}
        explanation={{
          what: "The Worker refused this event and it went to dead letters.",
          where: `Batch ${batch.id} on ${batch.farmId}, at ${batch.state}`,
          impact: final ? "The record stays unwritten until it is replayed." : "No step can happen until it is replayed.",
          next: logsLink ? "Replay the event." : "Ask an admin to replay the event.",
          correlationId: pending.correlationId,
          technical: pending.error ? <KeyValues items={[{ label: "Worker error", value: pending.error }, { label: "Event ID", value: pending.eventId }, { label: "Trace ID", value: pending.traceId }]} /> : undefined,
        }}
      />
    </Banner>
  );
}

const LOST_NEXT = "Live updates dropped, so this page may be behind. Read it again.";

export function EventBanner(props: EventBannerProps) {
  const { batch, now, lost = false } = props;
  const logsLink = useCanOpenLogs();
  const status = eventStatus(batch, now);
  const pending = batch.pending;
  if (!pending || status === "idle") return null;
  const final = isFinal(batch.state);
  const afterRecord = final ? "The batch stays final; only its record is outstanding." : "The next step opens when the record is written.";
  if (status === "blocked") return blockedBanner(props, pending, final, logsLink);
  const where = `Batch ${batch.id} on ${batch.farmId}, at ${batch.state}`;
  const technical = <KeyValues items={[{ label: "Event ID", value: pending.eventId }, { label: "Trace ID", value: pending.traceId }]} />;
  if (status === "delayed") {
    return (
      <Banner tone="warning" title="Compliance record is delayed">
        <ErrorDetail
          logsLink={logsLink}
          explanation={{ what: `Not confirmed since ${formatDateTime(pendingSince(batch))}.`, where, impact: afterRecord, next: lost ? LOST_NEXT : "Nothing yet. The Dispatcher retries by itself and holds delivery while the Worker is down.", correlationId: pending.correlationId, technical }}
        />
      </Banner>
    );
  }
  return (
    <Banner tone="info" title="Event in flight" live="off">
      <ErrorDetail explanation={{ what: "The compliance record for this step is being written.", where, impact: afterRecord, next: lost ? LOST_NEXT : "Nothing yet. This page updates by itself." }} />
    </Banner>
  );
}
