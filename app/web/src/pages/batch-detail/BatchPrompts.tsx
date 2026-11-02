import { client, type Tagged } from "../../api/client";
import { ApiError } from "../../api/problem";
import type { Batch, Me } from "../../api/types";
import { failNextGate } from "../../logic/batch/batchRules";
import { ConfirmDialog, FAIL_NEXT_CONFIRM } from "../shared";
import { InspectDialog } from "./InspectDialog";
import type { BatchActions } from "./useBatchActions";

async function failNextChecked(batch: Batch, me: Me | null): Promise<Tagged<Batch>> {
  // The API accepts this on a final batch, and with a lost socket this page can be old.
  const current = await client.getBatch(batch.farmId, batch.id);
  const refused = failNextGate(current.data, me);
  if (refused) throw new ApiError(409, "Conflict", refused, { local: true, request: { method: "POST", url: `/api/batches/${encodeURIComponent(batch.farmId)}/${encodeURIComponent(batch.id)}/fail-next` } });
  return client.failNext(batch.farmId, batch.id);
}

interface BatchPromptsProps {
  batch: Batch;
  etag: string;
  me: Me | null;
  actions: BatchActions;
  advanceTo: (to: Batch["state"]) => void;
}

export function BatchPrompts({ batch, etag, me, actions, advanceTo }: BatchPromptsProps) {
  const { action, prompt, setPrompt, inspectReason, setInspectReason, cancelInspect, perform } = actions;
  return (
    <>
      {prompt === "inspect" ? (
        <InspectDialog open busy={action.busy} reason={inspectReason} onReason={setInspectReason} onClose={cancelInspect} onSubmit={(passed, reason) => void perform(() => client.inspect(batch.farmId, batch.id, passed, reason, etag), passed ? "Inspection passed." : "Batch rejected.", passed ? "Inspected" : "Rejected")} />
      ) : null}
      <ConfirmDialog open={prompt === "close"} title="Close this batch" body="Closed is final. No further step can be recorded." confirm="Close batch" busy={action.busy} onClose={() => setPrompt(null)} onConfirm={() => advanceTo("Closed")} />
      <ConfirmDialog
        open={prompt === "fail-next"}
        {...FAIL_NEXT_CONFIRM}
        danger
        busy={action.busy}
        onClose={() => setPrompt(null)}
        onConfirm={() => void perform(() => failNextChecked(batch, me), "The next delivery of this batch will fail.")}
      />
    </>
  );
}
