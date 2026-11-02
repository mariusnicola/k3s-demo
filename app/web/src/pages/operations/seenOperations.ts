import type { ClusterOperation } from "../../api/monitorTypes";

const SEEN_KEPT = 50;
/** Per user: one person pressing Seen must not hide a failure from the admin who has to act on it. */
const seenStore = (user: string) => `bt.operations.seen.${user}`;

export function readSeen(user: string | undefined): Set<string> {
  if (!user) return new Set();
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(seenStore(user)) ?? "[]");
    return new Set(Array.isArray(stored) ? (stored as string[]) : []);
  } catch {
    return new Set();
  }
}

export function writeSeen(user: string, seen: Set<string>): void {
  try {
    localStorage.setItem(seenStore(user), JSON.stringify([...seen].slice(-SEEN_KEPT)));
  } catch {
    // Only the convenience is lost: the banner comes back after a reload.
  }
}

/**
 * A rollout keeps its id across runs, so the start tells them apart; the state lets a later failure show.
 * A run can go quiet, return and go quiet again, and only the last heartbeat tells those silences apart.
 */
export const seenKey = (operation: ClusterOperation) => [operation.id, operation.startedAt, operation.state, operation.state === "no-signal" ? operation.lastSeenAt : ""].join(":");
