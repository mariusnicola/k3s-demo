import { useEffect, useRef, useState } from "react";
import type { ClusterOperation } from "../../api/monitorTypes";
import { OperationToasts } from "../../ds";
import { useNow } from "../../live/clock";
import { useOperations } from "../../live/monitorFeed";
import { useSession } from "../../auth/session";
import { MONITOR_STALE_MS } from "../../config";
import { hasSteps, newTicks } from "../../logic/monitor/operations";
import { useCanOpenLogs } from "../shared";
import { items } from "./operationItems";
import { readSeen, writeSeen } from "./seenOperations";

function useEndedOperations(operations: ClusterOperation[]) {
  const [kept, setKept] = useState<ClusterOperation[]>([]);
  const [announcement, setAnnouncement] = useState("");
  const known = useRef(new Map<string, Set<number>>());
  useEffect(() => {
    const ticks = newTicks(operations, known.current);
    if (ticks) setAnnouncement(ticks);
    // The monitor drops a finished operation after seconds, and a checklist stays until seen.
    const ended = operations.filter((o) => hasSteps(o) && (o.state === "done" || o.state === "failed"));
    if (ended.length > 0) setKept((old) => [...old.filter((o) => !ended.some((e) => e.id === o.id)), ...ended]);
  }, [operations]);
  return { kept, announcement };
}

/** Shows on every page, for every user, for as long as the work on the cluster lasts. */
export function ClusterOperations() {
  const { operations, status, reportAt } = useOperations();
  const { me, logoutError, logoutPending } = useSession();
  const user = me?.user;
  const now = useNow();
  const [seen, setSeen] = useState(() => readSeen(user));
  const { kept, announcement } = useEndedOperations(operations);
  const logsLink = useCanOpenLogs();
  useEffect(() => setSeen(readSeen(user)), [user]);
  // An open socket can still go quiet; reports stop then, and the same age test as Health applies.
  const silent = reportAt !== null && now - reportAt > MONITOR_STALE_MS;
  const held = logoutPending || Boolean(logoutError);
  const live = status !== "lost" && !silent && !held;
  const retired = (o: ClusterOperation) => operations.some((current) => current.id === o.id || (o.work !== undefined && current.work === o.work));
  const all = [...operations, ...kept.filter((o) => !retired(o))];
  const markSeen = (key: string) => {
    const next = new Set(seen).add(key);
    if (user) writeSeen(user, next);
    setSeen(next);
  };
  const shown = items(all, now, seen, user ? markSeen : null, logsLink, live);
  // A lost socket proves nothing about the cluster, so it never reads as no operation.
  if (!live) shown.unshift({ id: "lost", state: "no-signal", title: "Cluster work state unknown", detail: held ? "The monitor stays closed until sign out finishes." : "The monitor is not answering.", elapsed: null });
  return <OperationToasts items={shown} announcement={announcement} />;
}
