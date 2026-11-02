import { hop } from "../../mocks/fixtures";
import { MAX_GAP_MS, PlaybackBuffer } from "./playback";

function at(ms: number) {
  return new Date(Date.parse("2026-10-30T10:00:00.000Z") + ms).toISOString();
}

function hops(count: number, startMs = 0, stepMs = 10) {
  return Array.from({ length: count }, (_, i) => hop({ at: at(startMs + i * stepMs), traceId: `t${i}` }));
}

describe("PlaybackBuffer", () => {
  it("REQ-BT-029 zero hops per tick leaves playback idle", () => {
    const buffer = new PlaybackBuffer();
    buffer.push([]);
    buffer.start();
    expect(buffer.size).toBe(0);
    expect(buffer.position).toBeNull();
    expect(buffer.advance(1000, 1)).toEqual([]);
  });

  it("REQ-BT-029 releases hops in order at speed 1", () => {
    const buffer = new PlaybackBuffer();
    buffer.push(hops(10));
    buffer.start();
    expect(buffer.advance(25, 1).map((h) => h.traceId)).toEqual(["t0", "t1", "t2"]);
    expect(buffer.remaining).toBe(7);
  });

  it.each([
    [0.25, 3],
    [0.5, 6],
    [1, 11],
    [2, 21],
  ] as const)("REQ-BT-029 speed %s releases %i hops per 100 ms of real time", (speed, expected) => {
    const buffer = new PlaybackBuffer();
    buffer.push(hops(50));
    buffer.start();
    expect(buffer.advance(100, speed)).toHaveLength(expected);
  });

  it("REQ-BT-029 500 hops in one tick all play and stay ordered", () => {
    const buffer = new PlaybackBuffer();
    buffer.push(hops(500, 0, 1));
    buffer.start();
    const played = [buffer.advance(250, 1), buffer.advance(250, 1), buffer.advance(250, 1)].flat();
    expect(played).toHaveLength(500);
    expect(played.at(-1)?.traceId).toBe("t499");
  });

  it("REQ-BT-029 buffer limit drops the oldest and counts what never played", () => {
    const buffer = new PlaybackBuffer(100);
    buffer.push(hops(100));
    buffer.start();
    buffer.advance(50, 1);
    const played = 100 - buffer.remaining;
    buffer.push(hops(30, 2000));
    expect(buffer.size).toBe(100);
    expect(buffer.droppedUnplayed).toBe(30 - played);
    expect(buffer.remaining).toBe(100);
  });

  it("REQ-BT-029 buffer limit counts nothing while not playing", () => {
    const buffer = new PlaybackBuffer(10);
    buffer.push(hops(25));
    expect(buffer.size).toBe(10);
    expect(buffer.droppedUnplayed).toBe(0);
  });

  it("REQ-BT-029 a single push larger than the limit keeps the newest", () => {
    const buffer = new PlaybackBuffer(5);
    buffer.push(hops(500));
    buffer.start();
    expect(buffer.size).toBe(5);
    expect(buffer.advance(1_000_000, 2)[0]?.traceId).toBe("t495");
  });

  it("REQ-BT-029 idle gaps are skipped", () => {
    const buffer = new PlaybackBuffer();
    buffer.push([...hops(1), hop({ at: at(60_000), traceId: "late" })]);
    buffer.start();
    buffer.advance(10, 1);
    expect(buffer.advance(10, 1).map((h) => h.traceId)).toEqual(["late"]);
    expect(MAX_GAP_MS).toBeLessThan(60_000);
  });

  it("REQ-BT-029 hops arriving during playback are reached", () => {
    const buffer = new PlaybackBuffer();
    buffer.start();
    buffer.push(hops(2));
    expect(buffer.advance(100, 1)).toHaveLength(2);
    expect(buffer.remaining).toBe(0);
  });

  it("REQ-BT-029 restart replays from the oldest held hop", () => {
    const buffer = new PlaybackBuffer();
    buffer.push(hops(3));
    buffer.start();
    buffer.advance(1000, 1);
    buffer.start();
    expect(buffer.remaining).toBe(3);
  });
});
