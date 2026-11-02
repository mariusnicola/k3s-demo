import { vi } from "vitest";
import { REQUEST_LIMIT_MS } from "../config";
import { batch } from "../mocks/fixtures";
import { installApi, useFakeSockets } from "../mocks/harness";
import { client } from "./client";
import { ApiError, describeProblem } from "./problem";

type Call = { url: string; init: RequestInit };

/** Like a real fetch: nothing comes back, and an abort rejects with the signal's reason. */
function silent() {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", (url: string, init: RequestInit) => {
    calls.push({ url, init });
    return new Promise((_, reject) => init.signal!.addEventListener("abort", () => reject(init.signal!.reason)));
  });
  return calls;
}

const read = (signal?: AbortSignal) => client.getBatch("farm-a", "b-1", signal).catch((e: unknown) => e);
const write = () => client.advance("farm-a", "b-1", "Dose2", '"v1"').catch((e: unknown) => e);

beforeEach(() => {
  useFakeSockets();
  vi.useFakeTimers();
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("REQ-BT-050 a request nothing answers ends", () => {
  it("REQ-BT-050 a read fails at the limit, once, with the ID sent and says how long it waited", async () => {
    const calls = silent();
    const failed = read();
    await vi.advanceTimersByTimeAsync(REQUEST_LIMIT_MS);
    const error = (await failed) as ApiError;
    expect(error).toBeInstanceOf(ApiError);
    expect(calls).toHaveLength(1);
    const sent = (calls[0]!.init.headers as Record<string, string>)["X-Correlation-Id"];
    const view = describeProblem(error);
    expect(view.message).toBe(`No usable answer came back within ${REQUEST_LIMIT_MS / 1000} s.`);
    expect(view.title).toBe("No usable answer");
    expect(view.correlationId).toBe(sent);
    expect(view.recovery).toBe("retry");
  });

  it("REQ-BT-050 a read a page can cancel still has the limit", async () => {
    silent();
    const failed = read(new AbortController().signal);
    await vi.advanceTimersByTimeAsync(REQUEST_LIMIT_MS);
    expect(describeProblem(await failed).message).toMatch(/No usable answer came back within/);
  });

  it("REQ-BT-050 not before the limit", async () => {
    silent();
    let done = false;
    void read().then(() => (done = true));
    await vi.advanceTimersByTimeAsync(REQUEST_LIMIT_MS - 1);
    expect(done).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    expect(done).toBe(true);
  });

  it("REQ-BT-050 a write says its result is unknown, and is sent once", async () => {
    const calls = silent();
    const failed = write();
    await vi.advanceTimersByTimeAsync(REQUEST_LIMIT_MS);
    const view = describeProblem(await failed);
    expect(calls).toHaveLength(1);
    expect(view.title).toBe("The result is unknown");
    expect(view.message).toBe(`No usable answer came back within ${REQUEST_LIMIT_MS / 1000} s.`);
  });

  it("REQ-BT-050 a body that stops arriving ends too", async () => {
    vi.stubGlobal("fetch", (_: string, init: RequestInit) =>
      Promise.resolve({
        ok: true,
        status: 200,
        headers: new Headers(),
        text: () => new Promise((_ok, reject) => init.signal!.addEventListener("abort", () => reject(init.signal!.reason))),
      }),
    );
    const failed = read();
    await vi.advanceTimersByTimeAsync(REQUEST_LIMIT_MS);
    const view = describeProblem(await failed);
    expect(view.message).toBe(`No usable answer came back within ${REQUEST_LIMIT_MS / 1000} s.`);
    expect(view.title).toBe("No usable answer");
  });

  it("REQ-BT-050 the page leaving still reads as an abort, not a failure", async () => {
    silent();
    const leaving = new AbortController();
    const failed = read(leaving.signal);
    leaving.abort();
    const error = (await failed) as DOMException;
    expect(error.name).toBe("AbortError");
  });

  it("REQ-BT-050 a refusal whose body stalls is timed out, once, not retried", async () => {
    let calls = 0;
    vi.stubGlobal("fetch", (_: string, init: RequestInit) => {
      calls += 1;
      return Promise.resolve({
        ok: false,
        status: 503,
        statusText: "Service Unavailable",
        headers: new Headers(),
        json: () => new Promise((_ok, reject) => init.signal!.addEventListener("abort", () => reject(init.signal!.reason))),
      });
    });
    const failed = read();
    await vi.advanceTimersByTimeAsync(REQUEST_LIMIT_MS);
    const view = describeProblem(await failed);
    expect(view.message).toMatch(/No usable answer came back within/);
    expect(view.title).toBe("No usable answer");
    expect(calls).toBe(1);
  });

  it("REQ-BT-050 a write whose refusal body stalls says its result is unknown", async () => {
    vi.stubGlobal("fetch", (_: string, init: RequestInit) =>
      Promise.resolve({
        ok: false,
        status: 503,
        statusText: "Service Unavailable",
        headers: new Headers(),
        json: () => new Promise((_ok, reject) => init.signal!.addEventListener("abort", () => reject(init.signal!.reason))),
      }),
    );
    const failed = write();
    await vi.advanceTimersByTimeAsync(REQUEST_LIMIT_MS);
    expect(describeProblem(await failed).title).toBe("The result is unknown");
  });

  it("REQ-BT-050 a refusal whose body is not JSON keeps its status", async () => {
    vi.stubGlobal("fetch", () => Promise.resolve(new Response("<html>", { status: 500 })));
    expect(((await read()) as ApiError).status).toBe(500);
  });

  it("REQ-BT-050 an answered request leaves no timer behind", async () => {
    installApi({ "GET /api/batches/farm-a/b-1": { etag: '"v1"', body: batch() } });
    await read();
    expect(vi.getTimerCount()).toBe(0);
  });
});
