import { client } from "../../api/client";
import type { LogFilters, ServiceLogs } from "../../api/types";
import { useResource } from "../../live/useResource";
import { readsServiceLog } from "../../logic/monitor/logs";

const NO_ENTRIES: ServiceLogs = { matched: 0, droppedByLimit: 0, evicted: 0, browserEvicted: 0, entries: [] };

/** Separate from the Line's read on purpose: this one failing must leave the Line's entries, counts and feed alone. */
export function useServiceLogs(filters: LogFilters) {
  return useResource(
    (signal) => (readsServiceLog(filters) ? client.serviceLogs(filters, signal).then((r) => r.data) : Promise.resolve(NO_ENTRIES)),
    [JSON.stringify(filters)],
  );
}
