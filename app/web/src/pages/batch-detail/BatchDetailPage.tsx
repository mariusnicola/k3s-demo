import { useState } from "react";
import { useParams } from "react-router-dom";
import { client, type Tagged } from "../../api/client";
import type { Batch, Me } from "../../api/types";
import { useSession } from "../../auth/session";
import { useNow } from "../../live/clock";
import { useHopState, useLiveBatch } from "../../live/hopFeed";
import { type Feed } from "../../live/useLiveSocket";
import { useMissedRead, useResource, type MissedRead, type Resource } from "../../live/useResource";
import { STATE_LABELS, can, eventStatus, failNextGate, replayGate, stepGate } from "../../logic/batch/batchRules";
import { Banner, Grid, PageHeader, Panel, Skeleton, useDocumentTitle } from "../../ds";
import { EventBanner } from "../EventBanner";
import { MissedBanner, ProblemBanner, ProblemState } from "../shared";
import { AdminPanel, DetailsPanel } from "./BatchFacts";
import { BatchHistory } from "./BatchHistory";
import { BatchPrompts } from "./BatchPrompts";
import { BatchStatusRow } from "./BatchStatusRow";
import { NextStepPanel } from "./NextStepPanel";
import { useBatchActions, type BatchActions } from "./useBatchActions";

interface BatchViewProps {
  batch: Batch;
  etag: string;
  me: Me | null;
  now: number;
  resource: Resource<Tagged<Batch>>;
  feed: Feed;
  lostRead: MissedRead;
  actions: BatchActions;
}

function BatchView({ batch, etag, me, now, resource, feed, lostRead, actions }: BatchViewProps) {
  const { action, notice, perform, setPrompt, reloadAfterRefusal } = actions;
  const replay = replayGate(batch, me, now);
  const replayEvent = () => void perform(() => client.replay(batch.farmId, batch.id), "Replay requested. The event goes back to the queue.");
  const advanceTo = (to: Batch["state"]) => void perform(() => client.advance(batch.farmId, batch.id, to, etag), `Moved to ${STATE_LABELS[to]}.`, to);

  return (
    <>
      <PageHeader
        title={batch.product}
        back={{ to: "/batches", label: "All batches" }}
        subtitle={batch.farmId}
      />
      {lostRead.missed ? <MissedBanner onRead={lostRead.readAgain} /> : null}
      <BatchStatusRow batch={batch} feed={feed} now={now} updatedAt={resource.updatedAt} missed={lostRead.missed} />
      {resource.error ? <ProblemBanner error={resource.error} onRetry={resource.reload} onReload={resource.reload} /> : null}
      {action.error ? <ProblemBanner error={action.error} onReload={reloadAfterRefusal} onDismiss={action.clearError} /> : null}
      {notice && notice.states.includes(batch.state) && eventStatus(batch, now) !== "blocked" ? <Banner tone="success" title={notice.text} /> : null}
      <EventBanner batch={batch} now={now} lost={lostRead.missed} onReplay={replayEvent} replayReason={replay} replaying={action.busy} />
      <Grid min="20rem">
        <NextStepPanel batch={batch} gate={stepGate(batch, me, now)} busy={action.busy} setPrompt={setPrompt} advanceTo={advanceTo} />
        <DetailsPanel batch={batch} />
        {can(me, "ops.admin") ? <AdminPanel failNext={failNextGate(batch, me)} onFailNext={() => setPrompt("fail-next")} /> : null}
      </Grid>
      <BatchHistory steps={batch.steps} />
      <BatchPrompts batch={batch} etag={etag} me={me} actions={actions} advanceTo={advanceTo} />
    </>
  );
}

export function BatchDetailPage() {
  const { farmId = "", id = "" } = useParams();
  const { me } = useSession();
  const now = useNow();
  const resource = useResource((signal) => client.getBatch(farmId, id, signal), [farmId, id]);
  const [applied, setApplied] = useState<Tagged<Batch> | null>(null);
  const live = useLiveBatch(farmId, id, resource.data, applied);
  const { feed } = useHopState();
  const lostRead = useMissedRead(feed.state.status, resource);
  const batch = live?.data;
  const actions = useBatchActions(batch, resource, setApplied);
  useDocumentTitle(batch?.product ?? "Batch");

  if (!batch) {
    return (
      <>
        <PageHeader title="Batch" back={{ to: "/batches", label: "All batches" }} />
        <Panel>{resource.error ? <ProblemState error={resource.error} onRetry={resource.reload} /> : <Skeleton rows={6} label="Loading batch" />}</Panel>
      </>
    );
  }

  return <BatchView batch={batch} etag={live?.etag ?? ""} me={me} now={now} resource={resource} feed={feed} lostRead={lostRead} actions={actions} />;
}
