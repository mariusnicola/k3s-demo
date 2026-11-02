import type { LineLogs, ServiceLogs } from "../../api/types";

export type ServiceRead = "read" | "pending" | "failed";

export interface ListReads {
  lineFailed: boolean;
  line: LineLogs | null;
  service: ServiceLogs | null;
  serviceRead: ServiceRead;
  /** The filters left neither log to read. */
  noLogRead: boolean;
  lost: boolean;
  missed: boolean;
  skipped: number;
  counterAhead: boolean;
}

function unreadLogs({ lineFailed, serviceRead }: ListReads): string[] {
  const unread = [...(lineFailed ? ["production line"] : []), ...(serviceRead === "failed" ? ["service"] : [])];
  if (unread.length === 0) return [];
  return [`The ${unread.length > 1 ? `${unread.join(" and ")} logs` : `${unread[0]} log`} could not be read.`];
}

function liveGap({ lost, missed }: ListReads): string[] {
  if (lost) return ["Live updates are lost, so newer entries are missing."];
  return missed ? ["Entries from the outage have not been read yet."] : [];
}

/** Why an empty list may not claim nothing is there. A notice that says entries are missing needs its reason here. */
export function listGaps(reads: ListReads): string[] {
  const { line, service, skipped, counterAhead, noLogRead } = reads;
  return [
    ...(noLogRead ? ["No log was read."] : []),
    ...unreadLogs(reads),
    ...liveGap(reads),
    ...(skipped > 0 ? ["Some entries came too fast to list."] : []),
    ...(counterAhead ? ["The Dashboard counts more errors than this log holds."] : []),
    ...(line && line.evicted > 0 ? ["Older Line entries left memory."] : []),
    ...(service && service.evicted > 0 ? ["Older service entries left memory."] : []),
    ...(service && service.browserEvicted > 0 ? ["Older browser reports left memory."] : []),
    ...((line?.droppedByLimit ?? 0) + (service?.droppedByLimit ?? 0) > 0 ? ["Some entries are past the read limit."] : []),
  ];
}
