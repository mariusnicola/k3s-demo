import { createContext, createElement, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useSession, type SessionStatus } from "../auth/session";
import { MONITOR_WS } from "../config";
import type { ChangeEntry, ClusterOperation, MonitorReport } from "../api/monitorTypes";
import { parseMonitorFrame, type MonitorFrame, type PerformanceFrame } from "../logic/feed/hops";
import { useLiveSocket, type Feed } from "./useLiveSocket";

const CHANGES_KEPT = 500;

interface MonitorValue {
  feed: Feed;
  /** The report socket state: other frames must not make an old report look fresh. */
  reportAt: number | null;
  changes: ChangeEntry[];
  operations: ClusterOperation[];
  listen: (onReport: (report: MonitorReport) => void) => () => void;
  listenPoints: (onPoint: (frame: PerformanceFrame) => void) => () => void;
}

const MonitorContext = createContext<MonitorValue | null>(null);

function useMonitorStore() {
  const [changes, setChanges] = useState<ChangeEntry[]>([]);
  const [operations, setOperations] = useState<ClusterOperation[]>([]);
  const [reportAt, setReportAt] = useState<number | null>(null);
  const listeners = useRef(new Set<(report: MonitorReport) => void>());
  const lastReport = useRef<MonitorReport | null>(null);
  const pointListeners = useRef(new Set<(frame: PerformanceFrame) => void>());

  const onFrame = useCallback((frame: MonitorFrame) => {
    if (frame.type === "report") {
      lastReport.current = frame.report;
      setReportAt(Date.now());
      listeners.current.forEach((listener) => listener(frame.report));
    } else if (frame.type === "changes") setChanges(frame.changes);
    else if (frame.type === "change") setChanges((old) => [...old, frame.change].slice(-CHANGES_KEPT));
    else if (frame.type === "metricPoint" || frame.type === "stagePoint") pointListeners.current.forEach((listener) => listener(frame));
    else setOperations(frame.operations);
  }, []);
  const clear = useCallback(() => {
    setChanges([]);
    setOperations([]);
    setReportAt(null);
    lastReport.current = null;
  }, []);
  return { changes, operations, reportAt, listeners, lastReport, pointListeners, onFrame, clear };
}

/** Counts changes between settled statuses. Settling for the first time is not one: that handshake already had the cookie. */
function useSessionTurns(status: SessionStatus): number {
  const turns = useRef({ settled: null as SessionStatus | null, count: 0 });
  if (status === "restoring") return turns.current.count;
  if (turns.current.settled !== null && turns.current.settled !== status) turns.current.count += 1;
  turns.current.settled = status;
  return turns.current.count;
}

function useMonitorListen({ listeners, lastReport, pointListeners }: ReturnType<typeof useMonitorStore>): Pick<MonitorValue, "listen" | "listenPoints"> {
  const listen = useCallback((onReport: (report: MonitorReport) => void) => {
    listeners.current.add(onReport);
    if (lastReport.current) onReport(lastReport.current);
    return () => {
      listeners.current.delete(onReport);
    };
  }, []);
  const listenPoints = useCallback((onPoint: (frame: PerformanceFrame) => void) => {
    pointListeners.current.add(onPoint);
    return () => {
      pointListeners.current.delete(onPoint);
    };
  }, []);
  return { listen, listenPoints };
}

/** One socket for the whole app: the operation banner needs it on every page. */
export function MonitorProvider({ children }: { children: ReactNode }) {
  const store = useMonitorStore();
  const { changes, operations, reportAt, onFrame, clear } = store;
  const { status, signIns, logoutPending, logoutError } = useSession();
  const turns = useSessionTurns(status) + signIns;
  // The cookie may still be good until logout has answered, and the monitor would take it for an admin's.
  const url = logoutPending || logoutError ? null : MONITOR_WS;
  const feed = useLiveSocket(url, parseMonitorFrame, onFrame, turns, clear);
  // On a change of session only: on a first load a visitor's frames may already be here, and the monitor will not resend them.
  // The report's age goes too: a socket that sends no reports must not inherit one and read as silent.
  useEffect(() => clear(), [turns]);
  const { listen, listenPoints } = useMonitorListen(store);
  const value = useMemo(() => ({ feed, reportAt, changes, operations, listen, listenPoints }), [feed, reportAt, changes, operations, listen, listenPoints]);
  return createElement(MonitorContext.Provider, { value }, children);
}

function useMonitor(): MonitorValue {
  const value = useContext(MonitorContext);
  if (!value) throw new Error("MonitorProvider is missing");
  return value;
}

export function useMonitorFeed(onReport: (report: MonitorReport) => void): Feed {
  const { feed, reportAt, listen } = useMonitor();
  const latest = useRef(onReport);
  latest.current = onReport;
  useEffect(() => listen((report) => latest.current(report)), [listen]);
  return useMemo(() => ({ reconnect: feed.reconnect, state: { ...feed.state, lastDataAt: reportAt } }), [feed, reportAt]);
}

export function useMetricPoints(onPoint: (frame: PerformanceFrame) => void): void {
  const { listenPoints } = useMonitor();
  const latest = useRef(onPoint);
  latest.current = onPoint;
  useEffect(() => listenPoints((frame) => latest.current(frame)), [listenPoints]);
}

export function useChanges(): { changes: ChangeEntry[]; status: Feed["state"]["status"] } {
  const { changes, feed } = useMonitor();
  return { changes, status: feed.state.status };
}

export function useOperations(): { operations: ClusterOperation[]; status: Feed["state"]["status"]; reportAt: number | null } {
  const { operations, feed, reportAt } = useMonitor();
  return { operations, status: feed.state.status, reportAt };
}
