import { useCallback, useState } from "react";
import { type MonitorReport } from "../../api/monitorTypes";
import { MONITOR_STALE_MS } from "../../config";
import { useMonitorFeed } from "../../live/monitorFeed";

/** Both ages count: a monitor that keeps sending an old report has a live socket and stale data. */
export function reportFresh(status: string, last: number | null, checkedAt: string | undefined, now: number): boolean {
  if (status !== "open" || last === null || checkedAt === undefined) return false;
  return now - last <= MONITOR_STALE_MS && now - Date.parse(checkedAt) <= MONITOR_STALE_MS;
}

export function useMonitorReport(now: number) {
  const [report, setReport] = useState<MonitorReport | null>(null);
  const onReport = useCallback((next: MonitorReport) => setReport(next), []);
  const monitorFeed = useMonitorFeed(onReport);
  const last = monitorFeed.state.lastDataAt;
  const fresh = reportFresh(monitorFeed.state.status, last, report?.checkedAt, now);
  // Before the first report nothing has gone stale yet.
  const stale = monitorFeed.state.status === "open" && report !== null && !fresh;
  return { report, monitorFeed, fresh, stale };
}
