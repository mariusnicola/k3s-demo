import { useEffect, useRef, useState, type Dispatch, type RefObject, type SetStateAction } from "react";
import { client } from "../../api/client";
import type { LineLogs, LineStatus, LogEntry, LogFilters } from "../../api/types";
import { LOG_LIMIT } from "../../config";
import { useLineFeed } from "../../live/useLineFeed";
import { useMissedRead, useResource, type Resource } from "../../live/useResource";
import { logsGap, mergeLogs, type LineFrame } from "../../logic/feed/live";
import { readsLine } from "../../logic/monitor/logs";

const NO_LINE_ROWS: LineLogs = { matched: 0, byReason: {}, droppedByLimit: 0, evicted: 0, entries: [] };

export interface HeldLogs {
  rows: LogEntry[];
  at: number;
}

interface MergeGuards {
  latest: RefObject<{ filters: LogFilters; loaded: boolean }>;
  early: RefObject<LineFrame[]>;
}

function useFrameState() {
  const [reply, setReply] = useState<LineLogs | null>(null);
  const [status, setStatus] = useState<LineStatus | null>(null);
  const [skipped, setSkipped] = useState(0);
  const [missed, setMissed] = useState(false);
  return { reply, setReply, status, setStatus, skipped, setSkipped, missed, setMissed };
}

function useMergeGuards(filters: LogFilters, logs: Resource<LineLogs>): MergeGuards {
  const latest = useRef({ filters, loaded: false });
  latest.current.filters = filters;
  // Every read, not only the first: a frame merged during one would be overwritten by its answer.
  if (logs.loading) latest.current.loaded = false;
  // Frames that land during the read wait here, or the read's answer would drop them.
  const early = useRef<LineFrame[]>([]);
  return { latest, early };
}

function useApplyAnswer(logs: Resource<LineLogs>, filters: LogFilters, { setReply, setSkipped, setMissed }: ReturnType<typeof useFrameState>, { latest, early }: MergeGuards) {
  useEffect(() => {
    setSkipped(early.current.reduce((count, frame) => count + (frame.type === "line" ? frame.entriesDropped : 0), 0));
    setMissed(false);
    setReply(logs.data ? early.current.reduce((merged, frame) => mergeLogs(merged, frame, filters, LOG_LIMIT), logs.data) : null);
    if (logs.data) early.current = [];
    latest.current.loaded = Boolean(logs.data);
    // The filters changed with the read, so its answer is the only thing that moves this.
  }, [logs.data]);
}

function useReplayAfterFailedRead(logs: Resource<LineLogs>, setReply: Dispatch<SetStateAction<LineLogs | null>>, { latest, early }: MergeGuards) {
  useEffect(() => {
    // A failed read has no answer to replay these onto, and the list on screen is still good.
    if (logs.loading || !logs.error) return;
    const frames = early.current;
    early.current = [];
    setReply((current) => (current ? frames.reduce((merged, frame) => mergeLogs(merged, frame, latest.current.filters, LOG_LIMIT), current) : current));
    latest.current.loaded = Boolean(logs.data);
  }, [logs.loading, logs.error]);
}

/** Statements run in this order on purpose: the helpers above are its steps, called top to bottom. */
export function useLogFrames(filters: LogFilters) {
  const [held, setHeld] = useState<HeldLogs | null>(null);
  const filterKey = JSON.stringify(filters);

  const logs = useResource((signal) => (readsLine(filters) ? client.lineLogs(filters, signal).then((r) => r.data) : Promise.resolve(NO_LINE_ROWS)), [filterKey]);
  const frames = useFrameState();
  const { reply, setReply, status, setStatus, skipped, setSkipped, missed, setMissed } = frames;
  const guards = useMergeGuards(filters, logs);
  const { latest, early } = guards;
  const reopened = useRef(false);

  const feed = useLineFeed((frame) => {
    setStatus(frame.status);
    if (frame.type === "line") setSkipped((count) => count + frame.entriesDropped);
    // The first snapshot of a page follows no outage, so nothing can be missing yet.
    if (reply && reopened.current && logsGap(reply, frame)) setMissed(true);
    if (frame.type === "snapshot") reopened.current = true;
    if (latest.current.loaded) setReply((current) => current && mergeLogs(current, frame, latest.current.filters, LOG_LIMIT));
    else early.current.push(frame);
  });

  useApplyAnswer(logs, filters, frames, guards);
  useEffect(() => setHeld(null), [filterKey]);
  const lostRead = useMissedRead(feed.state.status, logs);
  useReplayAfterFailedRead(logs, setReply, guards);

  return { held, setHeld, logs, reply, status, skipped, missed, feed, lostRead };
}
