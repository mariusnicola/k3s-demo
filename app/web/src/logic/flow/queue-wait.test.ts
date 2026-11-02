import { describe, expect, it } from "vitest";
import type { Hop, HopName } from "../../api/types";
import { QueueWait, WAIT_KEPT_MS } from "./queueWait";

const T0 = Date.parse("2026-11-01T12:00:00Z");
const hop = (name: HopName, traceId: string, atMs = T0, extra: Partial<Hop> = {}): Hop => ({ traceId, batchId: "b", farmId: "farm-a", hop: name, state: "Created", at: new Date(atMs).toISOString(), ok: true, ...extra });

describe("REQ-BT-028 Service Bus shows what waits in it", () => {
  it("REQ-BT-028 a step sent to the queue waits until it is delivered", () => {
    const wait = new QueueWait();
    wait.note([hop("bus", "a", T0), hop("bus", "b", T0 + 5_000)]);
    expect(wait.view(T0 + 73_000)).toEqual({ count: 2, oldestMs: 73_000 });
    wait.note([hop("deliver", "a", T0 + 73_000)]);
    expect(wait.view(T0 + 73_000)).toEqual({ count: 1, oldestMs: 68_000 });
  });

  it("REQ-BT-028 today's frames end the wait at the Worker, for want of a deliver hop", () => {
    const wait = new QueueWait();
    wait.note([hop("bus", "a"), hop("worker", "a", T0 + 40_000)]);
    expect(wait.view(T0 + 41_000).count).toBe(0);
  });

  it("REQ-BT-028 a Worker hop that beats its own send to the page leaves nothing waiting", () => {
    const wait = new QueueWait();
    wait.note([hop("worker", "a", T0 + 3), hop("bus", "a", T0 + 1)]);
    expect(wait.view(T0 + 197_000)).toEqual({ count: 0, oldestMs: null });
  });

  it("REQ-BT-028 a delivery that beats its send, in separate frames, leaves nothing waiting", () => {
    const wait = new QueueWait();
    wait.note([hop("deliver", "a", T0 + 2)]);
    wait.note([hop("bus", "a", T0 + 1)]);
    expect(wait.view(T0 + 1_000).count).toBe(0);
  });

  it("REQ-BT-028 what left the queue is forgotten after the kept time, so the page does not grow", () => {
    const wait = new QueueWait();
    wait.note([hop("worker", "a", T0)]);
    wait.view(T0 + WAIT_KEPT_MS + 1);
    wait.note([hop("bus", "a", T0 + WAIT_KEPT_MS + 2)]);
    expect(wait.view(T0 + WAIT_KEPT_MS + 3).count).toBe(1);
  });

  it("REQ-BT-028 a send the queue refused never waits", () => {
    const wait = new QueueWait();
    wait.note([hop("bus", "a", T0, { ok: false, reason: "send-failed" })]);
    expect(wait.view(T0).count).toBe(0);
  });

  it("REQ-BT-028 a dead letter leaves the queue", () => {
    const wait = new QueueWait();
    wait.note([hop("bus", "a"), hop("dispatcher", "a", T0 + 1_000, { ok: false, reason: "poison" })]);
    expect(wait.view(T0 + 2_000).count).toBe(0);
  });

  it("REQ-BT-028 a send whose delivery the page missed stops counting after the kept time", () => {
    const wait = new QueueWait();
    wait.note([hop("bus", "a")]);
    expect(wait.view(T0 + WAIT_KEPT_MS).count).toBe(1);
    expect(wait.view(T0 + WAIT_KEPT_MS + 1).count).toBe(0);
  });

  it("REQ-BT-028 nothing waiting has no oldest", () => {
    expect(new QueueWait().view(T0)).toEqual({ count: 0, oldestMs: null });
  });
});
