import { describe, expect, it } from "vitest";
import { RATE_WINDOW_MS, achievedRate, keepSamples, type RateSample } from "./lineRate";

const T0 = Date.parse("2026-11-01T12:00:00Z");
const sample = (s: number, started: number): RateSample => ({ at: new Date(T0 + s * 1000).toISOString(), started });

describe("REQ-BT-055 the Line card shows the rate it really reached", () => {
  it("REQ-BT-055 the rate is batches started over the time between samples", () => {
    expect(achievedRate([sample(0, 100), sample(10, 118)])).toBeCloseTo(1.8);
  });

  it("REQ-BT-055 too short a span gives no number rather than a noisy one", () => {
    expect(achievedRate([sample(0, 100)])).toBeNull();
    expect(achievedRate([sample(0, 100), sample(4, 110)])).toBeNull();
  });

  it("REQ-BT-055 a restarted Line, whose counter fell, starts the measure again", () => {
    expect(keepSamples([sample(0, 100), sample(10, 118)], sample(20, 3))).toEqual([sample(20, 3)]);
  });

  it("REQ-BT-055 only the window is kept", () => {
    const kept = keepSamples([sample(0, 1)], sample(RATE_WINDOW_MS / 1000 + 1, 2));
    expect(kept).toEqual([sample(RATE_WINDOW_MS / 1000 + 1, 2)]);
  });
});
