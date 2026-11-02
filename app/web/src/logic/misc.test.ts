import { vi } from "vitest";
import { ApiError, STATUS_NETWORK, describeProblem } from "../api/problem";
import { REPORT, hop } from "../mocks/fixtures";
import { formatAge, formatCount, formatDateTime, formatTime, shortId } from "./format";
import { HopDeduper, checksFailing, liveHops, nodeHealth, padWords, parseReport } from "./feed/hops";
import { trailingThrottle } from "./throttle";

describe("trailingThrottle", () => {
  beforeEach(() => void vi.useFakeTimers());
  afterEach(() => void vi.useRealTimers());

  it("REQ-BT-032 runs once per window however many events arrive", () => {
    const run = vi.fn();
    const throttled = trailingThrottle(run, 1000);
    for (let i = 0; i < 500; i += 1) throttled();
    expect(run).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(1000);
    expect(run).toHaveBeenCalledTimes(2);
    vi.advanceTimersByTime(5000);
    expect(run).toHaveBeenCalledTimes(2);
  });

  it("REQ-BT-032 cancel drops the trailing run", () => {
    const run = vi.fn();
    const throttled = trailingThrottle(run, 1000);
    throttled();
    throttled();
    throttled.cancel();
    vi.advanceTimersByTime(5000);
    expect(run).toHaveBeenCalledTimes(1);
  });
});

describe("describeProblem", () => {
  it.each([
    [400, "fix-input"],
    [401, "sign-in"],
    [403, "back-to-batches"],
    [404, "back-to-batches"],
    [409, "reload-batch"],
    [412, "reload-batch"],
    [428, "reload-batch"],
    [500, "retry"],
    [503, "retry"],
    [STATUS_NETWORK, "retry"],
  ])("REQ-BT-036 status %i has a way out: %s", (status, recovery) => {
    const view = describeProblem(new ApiError(status, "t", null));
    expect(view.recovery).toBe(recovery);
    expect(view.title.length).toBeGreaterThan(0);
    expect(view.message.length).toBeGreaterThan(0);
  });

  it("REQ-BT-036 server detail is shown for a 409", () => {
    expect(describeProblem(new ApiError(409, "Conflict", "Replay needs a Blocked batch")).message).toBe("Replay needs a Blocked batch");
  });

  it("REQ-BT-036 an unknown thrown value still gives a view", () => {
    expect(describeProblem(new Error("boom")).recovery).toBe("retry");
  });
});

describe("parsers", () => {
  it("REQ-BT-026 reads a full report", () => {
    expect(parseReport(JSON.stringify(REPORT))?.caps).toHaveLength(1);
  });
  it("REQ-BT-026 a report without nodes, pods or caps still parses", () => {
    const parsed = parseReport(JSON.stringify({ checkedAt: "x", healthy: false, checks: [{ name: "a", healthy: false, error: "down" }] }));
    expect(parsed?.nodes).toBeUndefined();
    expect(checksFailing(parsed!)).toHaveLength(1);
  });
  it("REQ-BT-026 junk is not a report", () => {
    expect(parseReport('{"healthy":true}')).toBeNull();
  });
  it("REQ-BT-030 no station reads ok when the monitor is not fresh", () => {
    const stale = nodeHealth({ ...REPORT, checks: [{ name: "cosmos", healthy: true, error: null }] }, false);
    expect(Object.values(stale).every((v) => v.state === "unknown" && v.text === "Stale")).toBe(true);
  });
  it("REQ-BT-026 stations follow their checks when fresh", () => {
    const health = nodeHealth({ ...REPORT, checks: [{ name: "cosmos", healthy: true, error: null }, { name: "service bus", healthy: false, error: "x" }] }, true);
    expect(health).toMatchObject({ cosmos: { state: "ok" }, bus: { state: "failing", text: "Failing" }, worker: { state: "unknown" } });
  });
  it("REQ-BT-026 a passing Service Bus check says only what it proves", () => {
    const health = nodeHealth({ ...REPORT, checks: [{ name: "service bus", healthy: true, error: null }] }, true);
    expect(health.bus).toEqual({ state: "ok", text: "Emulator answers" });
  });
  it("REQ-BT-026 a container reads only through a check that reaches it", () => {
    const passing = { ...REPORT, checks: [{ name: "ingress /line", healthy: true, error: null }, { name: "service bus", healthy: true, error: null }] };
    expect(padWords(passing, true)).toEqual({ Monitor: "Reporting", Line: "Answers", "Service Bus SQL": "Implied by Service Bus", MongoDB: "Not reported" });
    expect(padWords({ ...REPORT, checks: [{ name: "ingress /line", healthy: false, error: "x" }] }, true)).toMatchObject({ Line: "Failing", "Service Bus SQL": "Not reported" });
    expect(padWords(passing, false)).toEqual({ Monitor: "Stale", Line: "Stale", "Service Bus SQL": "Stale", MongoDB: "Stale" });
    expect(padWords(null, true)).toEqual({});
  });
  it("REQ-BT-026 a report carries the leader, and a terminating pod is not ready", () => {
    expect(parseReport(JSON.stringify({ ...REPORT, dispatcherLeader: "dispatcher-a" }))?.dispatcherLeader).toBe("dispatcher-a");
    expect(parseReport(JSON.stringify(REPORT))?.dispatcherLeader).toBeNull();
    const pod = { namespace: "batch-tracker", name: "worker-a", node: "n", phase: "Running", ready: true, terminating: true, cpuLimitMilli: 0, memoryLimitMi: 0, cpuUsageMilli: null, memoryUsageMi: null };
    expect(nodeHealth({ ...REPORT, pods: [pod], checks: [] }, true).worker).toEqual({ state: "failing", text: "0 of 1 pods ready" });
  });
  it("REQ-BT-026 Dispatcher and Worker read their pods from the report", () => {
    const pod = (name: string, ready: boolean) => ({ namespace: "batch-tracker", name, node: "n", phase: "Running", ready, cpuLimitMilli: 0, memoryLimitMi: 0, cpuUsageMilli: null, memoryUsageMi: null });
    const pods = [pod("worker-a", true), pod("worker-b", false), pod("dispatcher-a", false)];
    const health = nodeHealth({ ...REPORT, pods, checks: [] }, true);
    expect(health.worker).toEqual({ state: "ok", text: "1 of 2 pods ready" });
    expect(health.dispatcher).toEqual({ state: "failing", text: "0 of 1 pods ready" });
  });
});

describe("hop history", () => {
  it("REQ-BT-028 repeats are dropped, new hops are kept", () => {
    const deduper = new HopDeduper();
    expect(deduper.fresh([hop(), hop()])).toHaveLength(1);
    expect(deduper.fresh([hop(), hop({ hop: "bus" })])).toHaveLength(1);
  });
  it("REQ-BT-028 the same step retried with a new time is a new hop", () => {
    const deduper = new HopDeduper();
    deduper.fresh([hop()]);
    expect(deduper.fresh([hop({ at: "2026-10-30T10:02:00.000Z" })])).toHaveLength(1);
  });
  it("REQ-BT-028 history older than the frame's newest hop draws no dot", () => {
    const frame = [hop({ traceId: "old", at: "2026-10-30T09:00:00.000Z" }), hop({ traceId: "new" })];
    expect(liveHops(frame).map((h) => h.traceId)).toEqual(["new"]);
  });
  it("REQ-BT-028 a normal tick keeps every hop", () => {
    expect(liveHops([hop(), hop({ at: "2026-10-30T10:00:00.090Z" })])).toHaveLength(2);
  });
  it("REQ-BT-028 an empty frame is fine", () => {
    expect(liveHops([])).toEqual([]);
  });
});

describe("format", () => {
  it("REQ-BT-036 empty and bad dates read as words", () => {
    expect(formatDateTime(null)).toBe("Not recorded");
    expect(formatDateTime("nope")).toBe("Not recorded");
    expect(formatTime(null)).toBe("never");
  });
  it("REQ-BT-036 ages and counts", () => {
    expect(formatAge(5000)).toBe("5 s");
    expect(formatAge(125_000)).toBe("2 min 5 s");
    expect(formatAge(3_660_000)).toBe("1 h 1 min");
    expect(formatCount(0)).toBe("0");
    expect(formatCount(null)).toBe("0");
  });
  it("REQ-BT-036 short ids", () => {
    expect(shortId("short")).toBe("short");
    expect(shortId("3f1c2a7e-0d44-4f7b")).toBe("3f1c2a7e...");
  });
});
