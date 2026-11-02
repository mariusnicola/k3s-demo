import { vi } from "vitest";
import { installApi } from "../../mocks/harness";
import { ACCEPTED, REPORTS, UUID, posted, settle } from "../../mocks/reports";
import { postBrowserReport } from "../client";
import { reportFailure, reportThrown, startBrowserReports, type Failure } from "../reports";

const FAILURE: Failure = { kind: "render-fault", status: null, correlationId: null, message: "It broke" };
const HOLD_MS = 2_000;
/** What React's development build logs for a caught error. Its production build logs the value alone. */
const REACT_DEV_LINE = "%o\n\n%s\n\n%s\n";

let stop: () => void;

const leave = (event: string) => window.dispatchEvent(new Event(event));

function startWithSilentConsole() {
  stop();
  vi.spyOn(console, "error").mockImplementation(() => undefined);
  stop = startBrowserReports(postBrowserReport);
}

beforeEach(() => {
  window.history.pushState({}, "", "/batches/farm-a/b1");
  stop = startBrowserReports(postBrowserReport);
});

afterEach(() => {
  stop();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  vi.useRealTimers();
});

describe("B-076 a report during beforeunload", () => {
  it("B-076 is held, and goes out if the page is still alive after the hold", async () => {
    vi.useFakeTimers();
    const calls = installApi({ [REPORTS]: ACCEPTED });
    leave("beforeunload");
    const id = reportFailure(FAILURE);
    expect(id).toMatch(UUID);
    await vi.advanceTimersByTimeAsync(HOLD_MS - 1);
    expect(posted(calls)).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(1);
    expect(posted(calls).map((call) => (call.body as { id: string }).id)).toEqual([id]);
  });

  it("B-076 is dropped by pagehide, which means the page really left", async () => {
    vi.useFakeTimers();
    const calls = installApi({ [REPORTS]: ACCEPTED });
    leave("beforeunload");
    reportFailure(FAILURE);
    leave("pagehide");
    await vi.advanceTimersByTimeAsync(HOLD_MS * 2);
    expect(posted(calls)).toHaveLength(0);
  });

  it("B-076 reporting comes back after a Stop: the next failure goes at once", async () => {
    vi.useFakeTimers();
    const calls = installApi({ [REPORTS]: ACCEPTED });
    leave("beforeunload");
    await vi.advanceTimersByTimeAsync(HOLD_MS);
    reportFailure(FAILURE);
    await vi.advanceTimersByTimeAsync(0);
    expect(posted(calls)).toHaveLength(1);
  });

  it("B-076 a second beforeunload restarts the hold", async () => {
    vi.useFakeTimers();
    const calls = installApi({ [REPORTS]: ACCEPTED });
    leave("beforeunload");
    reportFailure(FAILURE);
    await vi.advanceTimersByTimeAsync(HOLD_MS - 1);
    leave("beforeunload");
    await vi.advanceTimersByTimeAsync(HOLD_MS - 1);
    expect(posted(calls)).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(1);
    expect(posted(calls)).toHaveLength(1);
  });

  it("B-076 stopping drops what is held", async () => {
    vi.useFakeTimers();
    const calls = installApi({ [REPORTS]: ACCEPTED });
    leave("beforeunload");
    reportFailure(FAILURE);
    stop();
    stop = startBrowserReports(postBrowserReport);
    await vi.advanceTimersByTimeAsync(HOLD_MS * 2);
    leave("beforeunload");
    await vi.advanceTimersByTimeAsync(HOLD_MS);
    expect(posted(calls)).toHaveLength(0);
  });
});

describe("B-076 a repeat shows the ID of a report that was sent", () => {
  it("B-076 a held report keeps its ID while held, and once sent", async () => {
    vi.useFakeTimers();
    const calls = installApi({ [REPORTS]: ACCEPTED });
    leave("beforeunload");
    const id = reportFailure(FAILURE);
    expect(reportFailure(FAILURE)).toBe(id);
    await vi.advanceTimersByTimeAsync(HOLD_MS);
    expect(reportFailure(FAILURE)).toBe(id);
    await vi.advanceTimersByTimeAsync(0);
    expect(posted(calls)).toHaveLength(1);
  });

  it("B-076 a report dropped by pagehide leaves no ID to reuse", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    leave("pagehide");
    const dropped = reportFailure(FAILURE);
    leave("pageshow");
    const sent = reportFailure(FAILURE);
    await settle();
    expect(sent).not.toBe(dropped);
    expect(posted(calls).map((call) => (call.body as { id: string }).id)).toEqual([sent]);
  });

  it("B-076 a report held then dropped leaves no ID to reuse", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    leave("beforeunload");
    const dropped = reportFailure(FAILURE);
    leave("pagehide");
    leave("pageshow");
    const sent = reportFailure(FAILURE);
    await settle();
    expect(sent).not.toBe(dropped);
    expect(posted(calls)).toHaveLength(1);
  });

  it("B-076 with no sender nothing was sent, so nothing is remembered", () => {
    stop();
    expect(reportFailure(FAILURE)).not.toBe(reportFailure(FAILURE));
    stop = startBrowserReports(postBrowserReport);
  });
});

describe("REQ-BT-056 a page that really left", () => {
  it("REQ-BT-056 after pagehide a failure gets its ID and sends nothing", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    leave("pagehide");
    expect(reportFailure(FAILURE)).toMatch(UUID);
    await settle();
    expect(posted(calls)).toHaveLength(0);
  });

  it("REQ-BT-056 a console error on the way out sends nothing", async () => {
    startWithSilentConsole();
    const calls = installApi({ [REPORTS]: ACCEPTED });
    leave("pagehide");
    console.error("Boom");
    await settle();
    expect(posted(calls)).toHaveLength(0);
  });

  it("REQ-BT-056 a page brought back from the cache reports again", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    leave("pagehide");
    leave("pageshow");
    reportFailure(FAILURE);
    await settle();
    expect(posted(calls)).toHaveLength(1);
  });

  it("REQ-BT-056 stopping forgets a page that left", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    leave("pagehide");
    stop();
    stop = startBrowserReports(postBrowserReport);
    reportFailure(FAILURE);
    await settle();
    expect(posted(calls)).toHaveLength(1);
  });
});

describe("B-076 a thrown primitive", () => {
  it.each([["a string", "Plain words"], ["a number", 7], ["null", null], ["undefined", undefined]])("B-066 %s keeps one ID and one report across redraws", async (_name, value) => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    const id = reportThrown(value);
    expect(reportThrown(value)).toBe(id);
    await settle();
    expect(posted(calls)).toHaveLength(1);
  });

  it("B-076 redraws keep it alive, because the fault is still on screen", () => {
    vi.useFakeTimers();
    installApi({ [REPORTS]: ACCEPTED });
    const id = reportThrown("Plain words");
    for (let draw = 0; draw < 5; draw += 1) {
      vi.advanceTimersByTime(50_000);
      expect(reportThrown("Plain words")).toBe(id);
    }
  });

  it("B-076 the memory ends with the repeat window once nothing draws it", () => {
    vi.useFakeTimers();
    installApi({ [REPORTS]: ACCEPTED });
    const id = reportThrown("Plain words");
    vi.advanceTimersByTime(61_000);
    expect(reportThrown("Plain words")).not.toBe(id);
  });

  it.each([
    ["production", ["Plain words"]],
    ["development", [REACT_DEV_LINE, "Plain words", "The above error occurred.", "React will try to recreate this."]],
  ])("B-066 React's own %s console call for it is no second report", async (_name, line) => {
    startWithSilentConsole();
    const calls = installApi({ [REPORTS]: ACCEPTED });
    reportThrown("Plain words");
    console.error(...line);
    await settle();
    expect(posted(calls)).toHaveLength(1);
  });

  it.each([
    ["the value with more words", ["Plain words", "and more"]],
    ["the value second", ["Saw:", "Plain words"]],
    ["another format holding the value", ["%o", "Plain words"]],
    ["a different value", ["Other words"]],
  ])("B-076 any other console line still reports: %s", async (_name, line) => {
    startWithSilentConsole();
    const calls = installApi({ [REPORTS]: ACCEPTED });
    reportThrown("Plain words");
    console.error(...line);
    await settle();
    expect(posted(calls)).toHaveLength(2);
  });

  it("B-076 the same value on the console after the window reports", async () => {
    startWithSilentConsole();
    const calls = installApi({ [REPORTS]: ACCEPTED });
    reportThrown("Plain words");
    vi.spyOn(Date, "now").mockReturnValue(Date.now() + 61_000);
    console.error("Plain words");
    await settle();
    expect(posted(calls)).toHaveLength(2);
  });

  it("B-066 stopping forgets it", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    const id = reportThrown("Plain words");
    stop();
    stop = startBrowserReports(postBrowserReport);
    expect(reportThrown("Plain words")).not.toBe(id);
    await settle();
    expect(posted(calls)).toHaveLength(2);
  });
});
