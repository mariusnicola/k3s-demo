import { client } from "../../../api/client";
import type { Batch, BatchState, Summary } from "../../../api/types";
import { BATCH_LIST_LIMIT } from "../../../config";
import { useHopState, useLiveBatches } from "../../../live/hopFeed";
import { useMissedRead, useResource } from "../../../live/useResource";
import { countText } from "../../../logic/batch/roleDashboard";
import { MissedBanner, ProblemBanner } from "../../shared";

export function useList(state?: BatchState) {
  const list = useResource((signal) => client.listBatches({ state, limit: BATCH_LIST_LIMIT }, signal).then((r) => r.data), [state]);
  const rows = useLiveBatches(list.data, (b) => !state || b.state === state, BATCH_LIST_LIMIT);
  const { feed } = useHopState();
  const lostRead = useMissedRead(feed.state.status, list);
  // Either side proves it: one row leaving a full read proves nothing, and pushes can fill a short one.
  return { list, rows, lostRead, capped: Math.max(list.data?.length ?? 0, rows?.length ?? 0) >= BATCH_LIST_LIMIT };
}

export type ListRead = ReturnType<typeof useList>;

/** Before the store is first read its zeros mean nothing. Loading promises an answer, so it is never said while the socket is lost. */
export function useKnownSummary(): { summary: Summary | null; loading: string | undefined } {
  const { state, feed } = useHopState();
  const { summary } = state;
  return { summary, loading: summary || feed.state.status === "lost" ? undefined : "Loading" };
}

export function countOf(read: ListRead, count: (rows: Batch[]) => number): string {
  if (read.rows) return countText(count(read.rows), read.capped);
  return read.list.error ? "Unknown" : "Loading";
}

export function MissedReads({ reads }: { reads: ListRead[] }) {
  if (!reads.some((read) => read.lostRead.missed)) return null;
  return <MissedBanner onRead={() => Promise.all(reads.map((read) => read.lostRead.readAgain()))} />;
}

/** The reads behind a count have no table of their own to show a failure in. */
export function FailedCounts({ reads }: { reads: ListRead[] }) {
  const failed = reads.filter((read) => read.list.error);
  if (failed.length === 0) return null;
  return <ProblemBanner error={failed[0]!.list.error} onRetry={() => Promise.all(failed.map((read) => read.list.reload()))} />;
}
