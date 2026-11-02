import { SUMMARY, batch, hop, lineLogs, logEntry, LINE_STOPPED } from "../../mocks/fixtures";
import { applyHopFrame, emptyHopState, logsGap, mergeBatches, mergeLogs, parseHopFrame, parseLineFrame, rowKey, type LineFrame } from "./live";

const filters = { level: "", reason: "", farm: "", batch: "", from: "", to: "" };
const row = (version: number, overrides = {}) => ({ etag: `"v${version}"`, batch: batch({ version, ...overrides }) });

describe("hop socket frames", () => {
  it("REQ-BT-032 hops arrive as a typed frame and bad hops are dropped", () => {
    const frame = parseHopFrame(JSON.stringify({ type: "hops", hops: [hop(), { nonsense: true }] }));
    expect(frame).toEqual({ type: "hops", hops: [hop()] });
  });

  it("REQ-BT-032 a bare array, broken JSON and unknown types are refused", () => {
    expect(parseHopFrame(JSON.stringify([hop()]))).toBeNull();
    expect(parseHopFrame("{nope")).toBeNull();
    expect(parseHopFrame(JSON.stringify({ type: "mystery" }))).toBeNull();
  });

  it("REQ-BT-032 a snapshot replaces all state", () => {
    const stale = applyHopFrame(emptyHopState(), { type: "batch", ...row(9, { id: "b-old" }) });
    const next = applyHopFrame(stale, { type: "snapshot", capped: false, at: "2026-10-31T10:00:00Z", hops: [], summary: SUMMARY, alerts: [], batches: [row(1)] });
    expect(next.summary).toEqual(SUMMARY);
    expect(next.alerts).toEqual([]);
    expect([...next.rows.keys()]).toEqual(["farm-a/b-1"]);
  });

  it("REQ-BT-032 the highest version wins, whatever order frames arrive in", () => {
    const newer = applyHopFrame(emptyHopState(), { type: "batch", ...row(5) });
    const after = applyHopFrame(newer, { type: "batch", ...row(4) });
    expect(after.rows.get("farm-a/b-1")?.batch.version).toBe(5);
    expect(applyHopFrame(after, { type: "batch", ...row(6) }).rows.get("farm-a/b-1")?.batch.version).toBe(6);
  });

  it("REQ-BT-032 summary and alerts frames replace only their own part", () => {
    const withBatch = applyHopFrame(emptyHopState(), { type: "batch", ...row(1) });
    const next = applyHopFrame(applyHopFrame(withBatch, { type: "summary", capped: false, summary: SUMMARY }), { type: "alerts", alerts: [] });
    expect(next.summary).toEqual(SUMMARY);
    expect(next.alerts).toEqual([]);
    expect(next.rows.size).toBe(1);
  });

  it("REQ-BT-032 rows kept in memory stay bounded and keep the most recently touched", () => {
    let state = emptyHopState();
    for (let i = 0; i < 1500; i += 1) state = applyHopFrame(state, { type: "batch", ...row(1, { id: `b-${i}` }) });
    expect(state.rows.size).toBe(1000);
    expect(state.rows.has("farm-a/b-1499")).toBe(true);
    expect(state.rows.has("farm-a/b-0")).toBe(false);
  });

  it("REQ-BT-032 a hops frame changes no row state", () => {
    const state = emptyHopState();
    expect(applyHopFrame(state, { type: "hops", hops: [hop()] })).toBe(state);
  });

  it("REQ-BT-032 a batch frame does not copy the kept rows, as frames arrive hundreds a second", () => {
    const state = emptyHopState();
    const next = applyHopFrame(state, { type: "batch", ...row(1) });
    expect(next).not.toBe(state);
    expect(next.rows).toBe(state.rows);
  });

  it("REQ-BT-032 each state starts with its own rows", () => {
    applyHopFrame(emptyHopState(), { type: "batch", ...row(1) });
    expect(emptyHopState().rows.size).toBe(0);
  });
});

describe("merging pushed rows into a list", () => {
  const rows = (...items: ReturnType<typeof row>[]) => new Map(items.map((r) => [rowKey(r.batch), r]));

  it("REQ-BT-032 a pushed row replaces its older copy in the list", () => {
    const merged = mergeBatches([batch({ version: 1, state: "Dose1" })], rows(row(2, { state: "Dose2" })), () => true, 200);
    expect(merged).toHaveLength(1);
    expect(merged[0]!.state).toBe("Dose2");
  });

  it("REQ-BT-032 an older pushed row does not undo the list", () => {
    expect(mergeBatches([batch({ version: 3, state: "Dose2" })], rows(row(2, { state: "Dose1" })), () => true, 200)[0]!.state).toBe("Dose2");
  });

  it("REQ-BT-032 a new batch joins at the top when it matches the filters", () => {
    const merged = mergeBatches([batch({ id: "b-old", createdAt: "2026-10-30T09:00:00Z" })], rows(row(1, { id: "b-new", createdAt: "2026-10-31T09:00:00Z" })), () => true, 200);
    expect(merged.map((b) => b.id)).toEqual(["b-new", "b-old"]);
  });

  it("REQ-BT-032 a row that moved out of the filter leaves the list", () => {
    const merged = mergeBatches([batch({ state: "Dose1", version: 1 })], rows(row(2, { state: "Dose2" })), (b) => b.state === "Dose1", 200);
    expect(merged).toEqual([]);
  });

  it("REQ-BT-032 the list stays within its limit", () => {
    const base = Array.from({ length: 200 }, (_, i) => batch({ id: `b-${i}` }));
    expect(mergeBatches(base, rows(row(1, { id: "b-new" })), () => true, 200)).toHaveLength(200);
  });
});

describe("line socket frames", () => {
  const frame = (overrides: Partial<LineFrame> = {}): LineFrame => ({ type: "line", at: "2026-10-31T10:00:00Z", status: LINE_STOPPED, entries: [], entriesDropped: 0, evicted: 0, ...overrides });

  it("REQ-BT-032 snapshot and line frames parse, anything else is refused", () => {
    expect(parseLineFrame(JSON.stringify(frame()))).toEqual(frame());
    expect(parseLineFrame(JSON.stringify(frame({ type: "snapshot" })))?.type).toBe("snapshot");
    expect(parseLineFrame(JSON.stringify({ type: "hops" }))).toBeNull();
    expect(parseLineFrame(JSON.stringify({ type: "line" }))).toBeNull();
    expect(parseLineFrame("{nope")).toBeNull();
  });

  it("REQ-BT-045 a new entry joins at the top and every count moves", () => {
    const merged = mergeLogs(lineLogs(), frame({ entries: [logEntry({ at: "2026-10-31T11:00:00.000Z", reason: "http-refused" })], evicted: 9 }), filters, 200);
    expect(merged.entries.map((e) => e.reason)).toEqual(["http-refused", "event-timeout"]);
    expect(merged.matched).toBe(2);
    expect(merged.byReason).toEqual({ "event-timeout": 1, "http-refused": 1 });
    expect(merged.evicted).toBe(9);
  });

  it("REQ-BT-045 an entry that fails the filters is not listed, though evicted still updates", () => {
    const merged = mergeLogs(lineLogs(), frame({ entries: [logEntry({ at: "2026-10-31T11:00:00.000Z", farm: "farm-b" })], evicted: 3 }), { ...filters, farm: "farm-a" }, 200);
    expect(merged.entries).toHaveLength(1);
    expect(merged.matched).toBe(1);
    expect(merged.evicted).toBe(3);
  });

  it.each([
    ["level", { level: "info" }],
    ["reason", { reason: "http-refused" }],
    ["batch number", { batch: "8" }],
    ["batch id", { batch: "b-other" }],
    ["from", { from: "2030-01-01T00:00:00Z" }],
    ["to", { to: "2020-01-01T00:00:00Z" }],
  ])("REQ-BT-045 the %s filter applies to pushed entries as the service applies it", (_name, set) => {
    expect(mergeLogs(lineLogs(), frame({ entries: [logEntry({ at: "2026-10-31T11:00:00.000Z" })] }), { ...filters, ...set }, 200).matched).toBe(1);
  });

  it("REQ-BT-045 an entry already listed is not counted twice, as a reopened snapshot repeats them", () => {
    expect(mergeLogs(lineLogs(), frame({ type: "snapshot", entries: [logEntry()] }), filters, 200).matched).toBe(1);
  });

  it("REQ-BT-045 a reopened snapshot that starts after the list shows entries were missed", () => {
    const later = frame({ type: "snapshot", entries: [logEntry({ at: "2026-10-31T12:00:00.000Z" })], entriesDropped: 40 });
    expect(logsGap(lineLogs(), later)).toBe(true);
  });

  it("REQ-BT-045 a snapshot that overlaps the list, holds everything or is a plain frame shows no gap", () => {
    expect(logsGap(lineLogs(), frame({ type: "snapshot", entries: [logEntry()], entriesDropped: 40 }))).toBe(false);
    expect(logsGap(lineLogs(), frame({ type: "snapshot", entries: [logEntry({ at: "2026-10-31T12:00:00.000Z" })], entriesDropped: 0 }))).toBe(false);
    expect(logsGap(lineLogs(), frame({ entries: [logEntry({ at: "2026-10-31T12:00:00.000Z" })], entriesDropped: 40 }))).toBe(false);
    expect(logsGap(lineLogs({ entries: [] }), frame({ type: "snapshot", entries: [logEntry()], entriesDropped: 40 }))).toBe(false);
  });

  it("REQ-BT-045 the list stays within its limit and counts what fell off", () => {
    const merged = mergeLogs(lineLogs({ entries: [logEntry()] }), frame({ entries: [logEntry({ at: "2026-10-31T11:00:00.000Z" })] }), filters, 1);
    expect(merged.entries).toHaveLength(1);
    expect(merged.droppedByLimit).toBe(1);
  });
});
