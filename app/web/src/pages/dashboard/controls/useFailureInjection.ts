import { useEffect, useState } from "react";
import { client } from "../../../api/client";
import type { Batch } from "../../../api/types";
import { useSession } from "../../../auth/session";
import { FAIL_TARGET_LIMIT } from "../../../config";
import { useHopState } from "../../../live/hopFeed";
import { useAction } from "../../../live/useAction";
import { failNextGate, isFinal } from "../../../logic/batch/batchRules";
import { newestFirst, rowKey } from "../../../logic/feed/live";

export interface FailTarget {
  farmId: string;
  id: string;
  product: string;
}

export const targetKey = (b: { farmId: string; id: string }) => `${b.farmId}/${b.id}`;

function useFailTargets() {
  const { me } = useSession();
  const { state, feed } = useHopState();
  // The summary is set by the snapshot, so it tells an empty list from one not yet received.
  const loaded = state.summary !== null;
  const [choice, setChoice] = useState("");
  const newest = newestFirst([...state.rows.values()].map((row) => row.batch)).slice(0, FAIL_TARGET_LIMIT);
  const targets = newest.filter((b) => !isFinal(b.state) && failNextGate(b, me) === null);
  const selected = targets.find((b) => targetKey(b) === choice) ?? targets[0];
  return { me, feed, loaded, setChoice, targets, selected, rows: state.rows };
}

interface Armed {
  target: FailTarget;
  version: number;
  state: string;
}

/** A newer row that no longer waits to fail, or that moved on, means the notice is stale. */
function useArmedNotice(rows: ReadonlyMap<string, { batch: Batch }>, pick: (value: string) => void) {
  const [armedAt, setArmed] = useState<Armed | null>(null);
  const row = armedAt ? rows.get(rowKey(armedAt.target))?.batch : undefined;
  const settled = armedAt !== null && row !== undefined && row.version > armedAt.version && (!row.failNext || row.state !== armedAt.state);
  useEffect(() => {
    if (settled) setArmed(null);
  }, [settled]);
  const setChoice = (value: string) => {
    setArmed(null);
    pick(value);
  };
  return { armed: armedAt?.target ?? null, setArmed, setChoice };
}

export function useFailureInjection() {
  const { me, feed, loaded, setChoice: pick, targets, selected, rows } = useFailTargets();
  const { armed, setArmed, setChoice } = useArmedNotice(rows, pick);
  const [refused, setRefused] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<FailTarget | null>(null);
  const action = useAction();

  const arm = () => {
    if (!confirming) return;
    const target = confirming;
    void action
      .run(async () => {
        setArmed(null);
        setRefused(null);
        // The API accepts this on a final batch, and a frame can still be on its way.
        const current = await client.getBatch(target.farmId, target.id);
        if (failNextGate(current.data, me) !== null) {
          setRefused(`${target.product} can no longer take a failure. Nothing was changed.`);
        } else {
          await client.failNext(target.farmId, target.id);
          setArmed({ target, version: current.data.version, state: current.data.state });
        }
      })
      .then(() => setConfirming(null));
  };
  return { feed, loaded, setChoice, targets, selected, armed, refused, confirming, setConfirming, action, arm };
}
