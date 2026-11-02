import { client } from "../../api/client";
import type { BatchState } from "../../api/types";
import { BATCH_LIST_LIMIT } from "../../config";
import { useHopState, useLiveBatches } from "../../live/hopFeed";
import { useMissedRead, useResource } from "../../live/useResource";

export function useBatchList(state: BatchState | "", farm: string) {
  const list = useResource((signal) => client.listBatches({ state: state || undefined, farmId: farm || undefined, limit: BATCH_LIST_LIMIT }, signal).then((r) => r.data), [state, farm]);
  const { state: live, feed } = useHopState();
  const lostRead = useMissedRead(feed.state.status, list);
  const summary = live.summary;
  const batches = useLiveBatches(list.data, (b) => (!state || b.state === state) && (!farm || b.farmId === farm), BATCH_LIST_LIMIT);
  return { list, feed, lostRead, summary, batches };
}
