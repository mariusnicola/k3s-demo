import { vi } from "vitest";
import { LiveSocket } from "../live/socket";
import { FakeSocket, installApi } from "../mocks/harness";
import { ACCEPTED, REPORTS, UUID, posted, settle } from "../mocks/reports";
import { client, postBrowserReport } from "./client";
import { ApiError } from "./problem";
import { startBrowserReports } from "./reports";

async function failed(read: Promise<unknown>): Promise<ApiError> {
  return read.then(
    () => {
      throw new Error("the call did not fail");
    },
    (error: ApiError) => error,
  );
}

let stop: () => void;

beforeEach(() => {
  window.history.pushState({}, "", "/batches/farm-a/b1");
  stop = startBrowserReports(postBrowserReport);
});

afterEach(() => {
  stop();
  vi.unstubAllGlobals();
});

describe("REQ-BT-056 what a leaving page breaks", () => {
  it("REQ-BT-056 an in-flight read cut off by the reload reports nothing", async () => {
    let cut: (reason: unknown) => void = () => undefined;
    const open = new Promise<never>((_resolve, reject) => (cut = reject));
    const calls = installApi({ "POST /api/batches": () => open, [REPORTS]: ACCEPTED });
    const write = failed(client.createBatch("farm-a", "Vaccine"));
    window.dispatchEvent(new Event("beforeunload"));
    cut(new TypeError("Failed to fetch"));
    expect((await write).status).toBe(0);
    window.dispatchEvent(new Event("pagehide"));
    await settle();
    expect(posted(calls)).toHaveLength(0);
  });

  it("REQ-BT-056 a socket closed by the reload reports nothing", async () => {
    const calls = installApi({ [REPORTS]: ACCEPTED });
    const sockets: FakeSocket[] = [];
    const socket = new LiveSocket<unknown>({ url: "ws://x/ws/hops", parse: () => null, onData: () => undefined, onState: () => undefined, create: (url) => (sockets.push(new FakeSocket(url)), sockets.at(-1)!) });
    socket.start();
    sockets[0]!.open();
    window.dispatchEvent(new Event("pagehide"));
    sockets[0]!.drop(1001);
    socket.stop();
    await settle();
    expect(posted(calls)).toHaveLength(0);
  });
});

describe("REQ-BT-056 a failed request", () => {
  it("REQ-BT-056 a read that gave up reports once, with the failed request's ID, and the error carries the report", async () => {
    const calls = installApi({ "GET /api/batches": { status: 503, body: { title: "Down", detail: "Cosmos is away" } }, [REPORTS]: ACCEPTED });
    const error = await failed(client.listBatches({}));
    await settle();
    expect(calls.filter((call) => call.url.includes("/api/batches")).length).toBeGreaterThan(1);
    expect(posted(calls)).toHaveLength(1);
    expect(error.reportId).toMatch(UUID);
    expect(posted(calls)[0]!.body).toEqual({
      id: error.reportId,
      kind: "request-failure",
      route: "/batches/farm-a/b1",
      status: 503,
      correlationId: error.correlationId,
      message: "GET /api/batches: Cosmos is away",
    });
  });

  it("REQ-BT-056 a read that recovered reports nothing", async () => {
    let tries = 0;
    const calls = installApi({ "GET /api/batches": () => ((tries += 1) === 1 ? { status: 503 } : { body: [] }), [REPORTS]: ACCEPTED });
    await client.listBatches({});
    await settle();
    expect(posted(calls)).toHaveLength(0);
  });

  it("REQ-BT-056 no answer goes with a null status and the ID the browser sent", async () => {
    const calls = installApi({ "POST /api/batches": () => Promise.reject(new TypeError("offline")), [REPORTS]: ACCEPTED });
    const error = await failed(client.createBatch("farm-a", "Vaccine"));
    await settle();
    expect(error.reportId).toMatch(UUID);
    expect(error.correlationId).toBe(calls[0]!.headers["X-Correlation-Id"]);
    expect(posted(calls)[0]!.body).toMatchObject({ status: null, correlationId: error.correlationId });
  });

  it.each([400, 401, 403, 404, 409, 412, 428, 429])("REQ-BT-056 a %i is an answer, not a browser failure: no report, no ID", async (status) => {
    const calls = installApi({ "POST /api/batches": { status, body: { title: "No" } }, [REPORTS]: ACCEPTED });
    const error = await failed(client.createBatch("farm-a", "Vaccine"));
    await settle();
    expect(error.status).toBe(status);
    expect(error.reportId).toBeNull();
    expect(posted(calls)).toHaveLength(0);
  });

  it.each([500, 502, 503, 504])("REQ-BT-056 a %i is a failure", async (status) => {
    const calls = installApi({ "POST /api/batches": { status, body: { title: "Down" } }, [REPORTS]: ACCEPTED });
    const error = await failed(client.createBatch("farm-a", "Vaccine"));
    await settle();
    expect(error.reportId).toMatch(UUID);
    expect(posted(calls)[0]!.body).toMatchObject({ status });
  });

  it("REQ-BT-056 the time limit is a failure, with a null status", async () => {
    const calls = installApi({ "POST /api/batches": () => Promise.reject(new DOMException("No answer in time", "TimeoutError")), [REPORTS]: ACCEPTED });
    const error = await failed(client.createBatch("farm-a", "Vaccine"));
    await settle();
    expect(error.timedOut).toBe(true);
    expect(error.reportId).toMatch(UUID);
    expect(posted(calls)[0]!.body).toMatchObject({ status: null, correlationId: error.correlationId });
  });

  it("REQ-BT-056 the message leaves out the read's query", async () => {
    const calls = installApi({ "GET /api/batches": { status: 500, body: { title: "No" } }, [REPORTS]: ACCEPTED });
    await failed(client.listBatches({ farmId: "farm-a" }));
    await settle();
    expect(posted(calls)[0]!.body).toMatchObject({ message: "GET /api/batches: No" });
  });

  it("REQ-BT-056 a read the page cancelled is no failure", async () => {
    const calls = installApi({ "GET /api/batches": () => Promise.reject(new DOMException("gone", "AbortError")), [REPORTS]: ACCEPTED });
    await expect(client.listBatches({})).rejects.toMatchObject({ name: "AbortError" });
    await settle();
    expect(posted(calls)).toHaveLength(0);
  });

  it("REQ-BT-056 a session check that finds no session is no failure, so it reports nothing", async () => {
    const calls = installApi({ "GET /api/session": { body: { me: null } }, [REPORTS]: ACCEPTED });
    await expect(client.restoreSession()).resolves.toMatchObject({ data: { me: null } });
    await settle();
    expect(posted(calls)).toHaveLength(0);
  });

  it("REQ-BT-056 the session check's own 429 reads as not signed in, so it reports nothing", async () => {
    const calls = installApi({ "GET /api/me": { status: 429, headers: { "Retry-After": "5" } }, [REPORTS]: ACCEPTED });
    await failed(client.restoreSession());
    await settle();
    expect(posted(calls)).toHaveLength(0);
  });

  it("REQ-BT-056 a session check that truly failed still reports", async () => {
    const calls = installApi({ "GET /api/me": { status: 500 }, [REPORTS]: ACCEPTED });
    const error = await failed(client.restoreSession());
    await settle();
    expect(error.reportId).toMatch(UUID);
    expect(posted(calls)).toHaveLength(1);
  });
});
