import { describe, expect, it } from "vitest";
import type { Summary } from "../../api/types";
import { applyHopFrame, emptyHopState, parseHopFrame } from "./live";

const PAUSE = { type: "pause", paused: true, reason: "fail-next", pod: "worker-a", at: "2026-11-01T12:00:00Z" };
const summary = { byState: {}, pending: 0, blocked: 0, flagged: 0 } as unknown as Summary;
const snapshot = (pause?: unknown) => JSON.stringify({ type: "snapshot", at: PAUSE.at, hops: [], summary, alerts: [], batches: [], capped: false, ...(pause === undefined ? {} : { pause }) });

describe("REQ-BT-028 the Worker pause reaches the map", () => {
  it("REQ-BT-028 a pause frame is read and held", () => {
    const frame = parseHopFrame(JSON.stringify(PAUSE))!;
    expect(applyHopFrame(emptyHopState(), frame).pause).toEqual({ paused: true, reason: "fail-next", pod: "worker-a", at: PAUSE.at });
  });

  it("REQ-BT-028 a malformed pause frame or an unknown frame is dropped, not thrown", () => {
    expect(parseHopFrame(JSON.stringify({ type: "pause", paused: "yes" }))).toBeNull();
    expect(parseHopFrame(JSON.stringify({ type: "something-new", a: 1 }))).toBeNull();
  });

  it("REQ-BT-028 a snapshot says the pause, or clears it, and an older one without the field clears it", () => {
    const held = applyHopFrame(emptyHopState(), parseHopFrame(JSON.stringify(PAUSE))!);
    expect(applyHopFrame(emptyHopState(), parseHopFrame(snapshot(PAUSE))!).pause).toMatchObject({ reason: "fail-next" });
    expect(applyHopFrame(held, parseHopFrame(snapshot(null))!).pause).toBeNull();
    expect(applyHopFrame(held, parseHopFrame(snapshot())!).pause).toBeNull();
  });

  it("REQ-BT-028 a cosmos hop from a pod not yet rolled is dropped, because the API hop already says it", () => {
    const frame = parseHopFrame(JSON.stringify({ type: "hops", hops: [{ traceId: "t", batchId: "b", at: PAUSE.at, hop: "cosmos" }] }));
    expect(frame).toMatchObject({ type: "hops", hops: [] });
  });

  it("REQ-BT-028 the two new hops pass the hop filter", () => {
    const hop = { traceId: "t", batchId: "b", at: PAUSE.at };
    const frame = parseHopFrame(JSON.stringify({ type: "hops", hops: [{ ...hop, hop: "deliver" }, { ...hop, hop: "settle" }, { ...hop, hop: "nonsense" }] }));
    expect(frame).toMatchObject({ type: "hops", hops: [{ hop: "deliver" }, { hop: "settle" }] });
  });
});
