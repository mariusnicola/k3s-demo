import { vi } from "vitest";
import { installApi } from "../mocks/harness";
import { ACCEPTED, REPORTS, UUID, posted, settle } from "../mocks/reports";
import mainSource from "../main.tsx?raw";
import { postBrowserReport } from "./client";
import { newReportId } from "./ids";
import { reportFailure, reportThrown, startBrowserReports, type Failure } from "./reports";

const FAILURE: Failure = { kind: "render-fault", status: null, correlationId: null, message: "It broke" };

let stop: () => void;

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

describe("REQ-BT-056 a report", () => {
  it("REQ-BT-056 an ID is a UUID, because the service refuses anything else", () => {
    expect(newReportId()).toMatch(UUID);
    expect(newReportId()).not.toBe(newReportId());
  });

  it("REQ-BT-056 sends the contract's fields and no more, once, under its own correlation ID", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    const id = reportFailure({ kind: "request-failure", status: 503, correlationId: "abc", message: "Down" });
    await settle();
    expect(posted(calls)).toHaveLength(1);
    const [call] = posted(calls);
    expect(call!.body).toEqual({ id, kind: "request-failure", route: "/batches/farm-a/b1", status: 503, correlationId: "abc", message: "Down" });
    expect(call!.headers["Content-Type"]).toBe("application/json");
    expect(call!.headers["X-Correlation-Id"]).toMatch(/^[0-9a-f]{32}$/);
    expect(id).toMatch(UUID);
  });

  it("REQ-BT-056 sends the path of the page, never its query or fragment", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    window.history.pushState({}, "", "/logs?correlationId=secret#row-3");
    reportFailure(FAILURE);
    await settle();
    expect(posted(calls)[0]!.body).toMatchObject({ route: "/logs" });
  });

  it("REQ-BT-056 cuts a long message to what the service keeps, so the body stays under 8 KiB", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    reportFailure({ ...FAILURE, message: "x".repeat(5000) });
    await settle();
    expect((posted(calls)[0]!.body as { message: string }).message).toHaveLength(2000);
  });

  it("REQ-BT-056 a status outside the contract goes as null", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    reportFailure({ ...FAILURE, status: 0 });
    await settle();
    expect(posted(calls)[0]!.body).toMatchObject({ status: null });
  });

  it("REQ-BT-056 keeps the first ID for a repeat and sends it once", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    const first = reportFailure({ ...FAILURE, correlationId: "one" });
    const again = reportFailure({ ...FAILURE, correlationId: "two" });
    await settle();
    expect(again).toBe(first);
    expect(posted(calls)).toHaveLength(1);
  });

  it("REQ-BT-056 tells different failures and different pages apart", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    const ids = new Set([reportFailure(FAILURE), reportFailure({ ...FAILURE, message: "Other" }), reportFailure({ ...FAILURE, kind: "console-error" }), reportFailure({ ...FAILURE, status: 500 })]);
    window.history.pushState({}, "", "/alerts");
    ids.add(reportFailure(FAILURE));
    await settle();
    expect(ids.size).toBe(5);
    expect(posted(calls)).toHaveLength(5);
  });

  it("REQ-BT-056 the same failure later counts as a new one", () => {
    vi.useFakeTimers();
    installApi({ [REPORTS]: ACCEPTED });
    const first = reportFailure(FAILURE);
    vi.advanceTimersByTime(59_000);
    expect(reportFailure(FAILURE)).toBe(first);
    vi.advanceTimersByTime(2_000);
    expect(reportFailure(FAILURE)).not.toBe(first);
  });

  it.each([
    ["is refused", { [REPORTS]: { status: 429, headers: { "Retry-After": "1" } } }],
    ["fails", { [REPORTS]: { status: 500 } }],
    ["gets no answer", { [REPORTS]: () => Promise.reject(new TypeError("offline")) }],
  ])("REQ-BT-056 a post that %s is lost: one try, no console line, no new report", async (_name, routes) => {
    const calls = installApi(routes);
    const shown = vi.spyOn(console, "error").mockImplementation(() => undefined);
    const id = reportFailure(FAILURE);
    await settle();
    expect(id).toMatch(UUID);
    expect(posted(calls)).toHaveLength(1);
    expect(shown).not.toHaveBeenCalled();
  });

  it("REQ-BT-056 a lost post leaves no unhandled rejection, which would report itself in a loop", async () => {
    const unhandled = vi.fn();
    process.on("unhandledRejection", unhandled);
    // A plain function: vi.fn watches the promise it returns, which would count as handling the rejection.
    vi.stubGlobal("fetch", () => Promise.reject(new TypeError("offline")));
    reportFailure(FAILURE);
    await settle();
    await settle();
    process.off("unhandledRejection", unhandled);
    expect(unhandled).not.toHaveBeenCalled();
  });

  it("REQ-BT-056 before the app starts reporting, an ID still comes back and nothing is sent", async () => {
    stop();
    const calls = installApi({ [REPORTS]: ACCEPTED });
    expect(reportFailure(FAILURE)).toMatch(UUID);
    await settle();
    expect(posted(calls)).toHaveLength(0);
    stop = startBrowserReports(postBrowserReport);
  });

  it("REQ-BT-056 the app turns reporting on", () => {
    expect(mainSource).toMatch(/^startBrowserReports\(postBrowserReport\);\r?$/m);
  });
});

describe("REQ-BT-056 a value thrown in the page", () => {
  it("REQ-BT-056 keeps one ID however often the page draws it", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    const thrown = new TypeError("Page bug");
    const id = reportThrown(thrown);
    expect(reportThrown(thrown)).toBe(id);
    vi.spyOn(Date, "now").mockReturnValue(Date.now() + 3_600_000);
    expect(reportThrown(thrown)).toBe(id);
    await settle();
    expect(id).toMatch(UUID);
    expect(posted(calls)).toEqual([expect.objectContaining({ body: expect.objectContaining({ id, kind: "render-fault", status: null, correlationId: null, message: "Page bug" }) })]);
  });

  it("REQ-BT-056 its console line is not a second report", async () => {
    stop();
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    stop = startBrowserReports(postBrowserReport);
    const calls = installApi({ [REPORTS]: ACCEPTED });
    const thrown = new TypeError("Page bug");
    reportThrown(thrown);
    console.error("%o", thrown);
    await settle();
    expect(posted(calls)).toHaveLength(1);
  });

  it("REQ-BT-056 a different error on the console still reports", async () => {
    stop();
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    stop = startBrowserReports(postBrowserReport);
    const calls = installApi({ [REPORTS]: ACCEPTED });
    reportThrown(new TypeError("Page bug"));
    console.error(new TypeError("Another bug"));
    await settle();
    expect(posted(calls)).toHaveLength(2);
  });

  it.each([["a string", "Plain words"], ["null", null], ["undefined", undefined]])("REQ-BT-056 %s thrown by a component reports too", (_name, value) => {
    expect(reportThrown(value)).toMatch(UUID);
  });
});

describe("REQ-BT-056 a console error", () => {
  it("REQ-BT-056 stays on the console and reports with no request behind it", async () => {
    stop();
    const shown = vi.spyOn(console, "error").mockImplementation(() => undefined);
    stop = startBrowserReports(postBrowserReport);
    const calls = installApi({ [REPORTS]: ACCEPTED });
    console.error("Boom", 7);
    await settle();
    expect(shown).toHaveBeenCalledWith("Boom", 7);
    expect(posted(calls)[0]!.body).toMatchObject({ kind: "console-error", status: null, correlationId: null, message: "Boom 7" });
  });

  it("REQ-BT-056 a value that cannot print leaves console.error working", () => {
    stop();
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    stop = startBrowserReports(postBrowserReport);
    installApi({ [REPORTS]: ACCEPTED });
    expect(() => console.error(Object.create(null))).not.toThrow();
  });

  it("REQ-BT-056 stopping puts console.error back", () => {
    stop();
    const own = console.error;
    const started = startBrowserReports(postBrowserReport);
    expect(console.error).not.toBe(own);
    started();
    expect(console.error).toBe(own);
    stop = startBrowserReports(postBrowserReport);
  });

  it("REQ-BT-056 an uncaught error and an unhandled rejection report as console errors", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    window.dispatchEvent(new ErrorEvent("error", { message: "Uncaught boom" }));
    const rejected = new Event("unhandledrejection") as Event & { reason: unknown };
    rejected.reason = new Error("Never caught");
    window.dispatchEvent(rejected);
    await settle();
    expect(posted(calls).map((call) => call.body)).toEqual([
      expect.objectContaining({ kind: "console-error", message: "Uncaught boom" }),
      expect.objectContaining({ kind: "console-error", message: "Never caught" }),
    ]);
  });
});
