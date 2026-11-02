import { useState, type Dispatch, type SetStateAction } from "react";
import type { Tagged } from "../../api/client";
import type { Batch } from "../../api/types";
import { useAction } from "../../live/useAction";
import type { Resource } from "../../live/useResource";

export type Prompt = "inspect" | "close" | "fail-next" | null;

export function useBatchActions(batch: Batch | undefined, resource: Resource<Tagged<Batch>>, setApplied: Dispatch<SetStateAction<Tagged<Batch> | null>>) {
  const action = useAction();
  const [prompt, setPrompt] = useState<Prompt>(null);
  // Stays while the batch is in the state it left or reached, so it survives a lost reread and leaves when someone else moves on.
  const [notice, setNotice] = useState<{ text: string; states: Batch["state"][] } | null>(null);
  const [inspectReason, setInspectReason] = useState("");
  const perform = async (work: () => Promise<Tagged<Batch>>, done: string, to?: Batch["state"]) => {
    setNotice(null);
    // The reply carries the new row and its ETag, so a write needs no read after it.
    const ok = await action.run(async () => setApplied(await work()));
    setPrompt(null);
    if (ok) {
      setNotice({ text: done, states: batch ? [batch.state, to ?? batch.state] : [] });
      setInspectReason("");
    } else {
      void resource.reload();
    }
  };

  const cancelInspect = () => {
    setPrompt(null);
    setInspectReason("");
  };

  const reloadAfterRefusal = () => {
    action.clearError();
    void resource.reload();
  };
  return { action, prompt, setPrompt, notice, inspectReason, setInspectReason, perform, cancelInspect, reloadAfterRefusal };
}

export type BatchActions = ReturnType<typeof useBatchActions>;
