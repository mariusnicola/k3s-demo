import { FAIL_TARGET_LIMIT } from "../../../config";
import { formatCount, shortId } from "../../../logic/format";
import { AppLink, Banner, Button, Cluster, EmptyState, Panel, Select, Skeleton, Text } from "../../../ds";
import { ConfirmDialog, FAIL_NEXT_CONFIRM, ProblemBanner } from "../../shared";
import { targetKey, useFailureInjection } from "./useFailureInjection";

function FailTargetPicker({ injection }: { injection: ReturnType<typeof useFailureInjection> }) {
  const { feed, loaded, setChoice, targets, selected, setConfirming } = injection;
  return (
    !loaded ? (
      feed.state.status === "lost" ? <Text tone="secondary">Batches are unknown until the live stream returns.</Text> : <Skeleton rows={2} label="Loading batches" />
    ) : targets.length === 0 ? (
      <EmptyState title="No batch can take a failure">
        Only an open batch not already set to fail qualifies, among the newest {formatCount(FAIL_TARGET_LIMIT)}. Create one or start the line.
      </EmptyState>
    ) : (
      <>
        <Select label="Batch" value={selected ? targetKey(selected) : ""} onChange={(e) => setChoice(e.target.value)} options={targets.map((b) => ({ value: targetKey(b), label: `${b.product}, ${b.farmId}, ${shortId(b.id)}` }))} />
        <Cluster>
          <Button variant="danger" onClick={() => setConfirming(selected ?? null)}>
            Fail next delivery
          </Button>
        </Cluster>
      </>
    )
  );
}

export function FailurePanel() {
  const injection = useFailureInjection();
  const { armed, refused, confirming, setConfirming, action, arm } = injection;

  return (
    <Panel
      title="Failure injection"
      description="The next delivery of the chosen batch is refused, the event dead-letters and the batch shows Blocked."
    >
      <FailTargetPicker injection={injection} />
      {armed ? (
        <Banner tone="success" title={`Set to fail: ${armed.product}`}>
          <AppLink to={`/batches/${encodeURIComponent(armed.farmId)}/${encodeURIComponent(armed.id)}`}>Open this batch</AppLink> to watch it block and to replay it.
        </Banner>
      ) : null}
      {refused ? <Banner tone="warning" title={refused} /> : null}
      {action.error ? <ProblemBanner error={action.error} onDismiss={action.clearError} /> : null}
      <ConfirmDialog
        open={confirming !== null}
        {...FAIL_NEXT_CONFIRM}
        body={confirming ? `${confirming.product}, ${confirming.farmId}, ${shortId(confirming.id)}. ${FAIL_NEXT_CONFIRM.body}` : ""}
        danger
        busy={action.busy}
        onClose={() => setConfirming(null)}
        onConfirm={arm}
      />
    </Panel>
  );
}
