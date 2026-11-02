import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { MAX_ATTEMPTS, backoffMs, retryAfterMs } from "../logic/backoff";
import { LiveSocket } from "../live/socket";
import { batch } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../mocks/harness";
import { client } from "./client";
import { setRetryWait } from "./retry";
import { ApiError, STATUS_NETWORK, describeProblem } from "./problem";

const ROUTE = "GET /api/batches/farm-a/b-1";
const READ = () => client.getBatch("farm-a", "b-1");

beforeEach(() => useFakeSockets());
afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("REQ-BT-050 delays", () => {
  const waits = (random: () => number) => Array.from({ length: MAX_ATTEMPTS - 1 }, (_, i) => backoffMs(i + 1, random));

  it("REQ-BT-050 the limit is one constant of 10", () => {
    expect(MAX_ATTEMPTS).toBe(10);
  });

  it("REQ-BT-050 each delay grows from 200 ms and stops at 30 s", () => {
    expect(waits(() => 1)).toEqual([200, 400, 800, 1600, 3200, 6400, 12800, 25600, 30000]);
  });

  it("REQ-BT-050 equal jitter keeps half fixed, so a wait never falls under half the delay", () => {
    expect(waits(() => 0)).toEqual([100, 200, 400, 800, 1600, 3200, 6400, 12800, 15000]);
    expect(backoffMs(1, () => 0.5)).toBe(150);
  });

  it("REQ-BT-050 all attempts span at least 40.5 s and at most 81 s", () => {
    const sum = (list: number[]) => list.reduce((a, b) => a + b, 0);
    expect(sum(waits(() => 0))).toBe(40_500);
    expect(sum(waits(() => 1))).toBe(81_000);
  });

  it("REQ-BT-050 the delay stays capped however long a socket keeps trying", () => {
    expect(backoffMs(5000, () => 1)).toBe(30_000);
  });

  it("REQ-BT-050 Retry-After reads as seconds or a date, and anything else is no answer", () => {
    expect(retryAfterMs("5", 0)).toBe(5000);
    expect(retryAfterMs("Thu, 01 Jan 1970 00:00:10 GMT", 0)).toBe(10_000);
    expect(retryAfterMs("soon", 0)).toBeNull();
    expect(retryAfterMs("-1", 0)).toBeNull();
    expect(retryAfterMs(null, 0)).toBeNull();
  });
});

describe("REQ-BT-050 the one retry path", () => {
  const waits: number[] = [];
  const fast = () => {
    waits.length = 0;
    setRetryWait(async (ms) => void waits.push(ms));
  };

  it.each([429, 502, 503, 504])("REQ-BT-050 a read that gets %i retries and passes", async (status) => {
    let reads = 0;
    installApi({ [ROUTE]: () => (++reads === 1 ? { status } : { etag: '"v1"', body: batch() }) });
    fast();
    expect((await READ()).data.id).toBe("b-1");
    expect(reads).toBe(2);
  });

  it("REQ-BT-050 a read that fails on the network retries and passes", async () => {
    let reads = 0;
    vi.stubGlobal("fetch", vi.fn(async () => {
      if (++reads === 1) throw new TypeError("down");
      return new Response(JSON.stringify(batch()));
    }));
    setRetryWait(async () => undefined);
    expect((await READ()).data.id).toBe("b-1");
    expect(reads).toBe(2);
  });

  it.each([400, 401, 403, 404, 409, 412, 428, 500])("REQ-BT-050 a read that gets %i is not retried", async (status) => {
    const calls = installApi({ [ROUTE]: { status } });
    fast();
    await expect(READ()).rejects.toMatchObject({ status });
    expect(calls).toHaveLength(1);
  });

  it("REQ-BT-050 attempt 10 is the last, and its failure is what the caller gets", async () => {
    const calls = installApi({ [ROUTE]: { status: 503, body: { title: "last" } } });
    fast();
    await expect(READ()).rejects.toMatchObject({ status: 503, title: "last" });
    expect(calls).toHaveLength(MAX_ATTEMPTS);
    expect(waits).toHaveLength(MAX_ATTEMPTS - 1);
  });

  it("REQ-BT-050 Retry-After above the delay is honoured", async () => {
    let reads = 0;
    installApi({ [ROUTE]: () => (++reads === 1 ? { status: 429, headers: { "Retry-After": "5" } } : { body: batch() }) });
    fast();
    await READ();
    expect(waits).toEqual([5000]);
  });

  it("REQ-BT-050 Retry-After below the delay leaves the delay", async () => {
    vi.spyOn(Math, "random").mockReturnValue(1);
    let reads = 0;
    installApi({ [ROUTE]: () => (++reads < 9 ? { status: 429, headers: { "Retry-After": "1" } } : { body: batch() }) });
    fast();
    await READ();
    expect(waits.at(-1)).toBe(25_600);
  });

  it("REQ-BT-050 a malformed Retry-After uses the delay", async () => {
    vi.spyOn(Math, "random").mockReturnValue(1);
    let reads = 0;
    installApi({ [ROUTE]: () => (++reads === 1 ? { status: 503, headers: { "Retry-After": "soon" } } : { body: batch() }) });
    fast();
    await READ();
    expect(waits).toEqual([200]);
  });

  it("REQ-BT-050 a Retry-After past the longest delay ends the retries and says when", async () => {
    const calls = installApi({ [ROUTE]: { status: 429, headers: { "Retry-After": "120" } } });
    fast();
    await expect(READ()).rejects.toMatchObject({ status: 429, retryAfterMs: 120_000 });
    expect(calls).toHaveLength(1);
  });

  it("REQ-BT-050 aborting during a wait stops at once, timer gone, no further call", async () => {
    const calls = installApi({ [ROUTE]: { status: 503 } });
    setRetryWait(undefined);
    vi.useFakeTimers();
    const abort = new AbortController();
    const read = client.getBatch("farm-a", "b-1", abort.signal).catch((e: unknown) => e);
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toHaveLength(1);
    abort.abort();
    expect(((await read) as DOMException).name).toBe("AbortError");
    expect(vi.getTimerCount()).toBe(0);
    await vi.advanceTimersByTimeAsync(120_000);
    expect(calls).toHaveLength(1);
  });

  it.each([429, 502, 503, 504])("REQ-BT-050 a write that gets %i is not repeated", async (status) => {
    const calls = installApi({ "POST /api/batches/farm-a/b-1/advance": { status } });
    fast();
    await expect(client.advance("farm-a", "b-1", "Dose2", '"v1"')).rejects.toBeInstanceOf(ApiError);
    expect(calls).toHaveLength(1);
  });

  it("REQ-BT-050 a create that fails on the network is not repeated", async () => {
    const fetcher = vi.fn().mockRejectedValue(new TypeError("down"));
    vi.stubGlobal("fetch", fetcher);
    await expect(client.createBatch("farm-a", "Lot")).rejects.toBeInstanceOf(ApiError);
    expect(fetcher).toHaveBeenCalledTimes(1);
  });
});

describe("REQ-BT-050 what the operator reads", () => {
  it("REQ-BT-050 a write lost on the network says the result is unknown and offers Reload", () => {
    const view = describeProblem(new ApiError(STATUS_NETWORK, "Network error", null, { resultUnknown: true }));
    expect(view.title).toBe("The result is unknown");
    expect(view.impact).toMatch(/may have saved/);
    expect(view.recovery).toBe("reload-batch");
  });

  it("REQ-BT-050 a read lost on the network keeps Try again", () => {
    expect(describeProblem(new ApiError(STATUS_NETWORK, "Network error", null)).recovery).toBe("retry");
  });

  it("REQ-BT-050 429 says when to try again", () => {
    const view = describeProblem(new ApiError(429, "Too many requests", null, { retryAfterMs: 120_000 }));
    expect(view.title).toBe("Too many requests");
    expect(view.next).toContain("120 s");
    expect(view.recovery).toBe("retry");
  });

  it("REQ-BT-050 TM-2397 a step lost on the network is sent once, says the result is unknown and offers Reload", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia"), [ROUTE]: { etag: '"v1"', body: batch() }, "POST /api/batches/farm-a/b-1/advance": () => Promise.reject(new TypeError("down")) });
    renderApp("/batches/farm-a/b-1");
    await userEvent.click(await screen.findByRole("button", { name: "Move to Dose 2" }));
    expect(await screen.findByText("The result is unknown")).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Reload batch" }).length).toBeGreaterThan(0);
    expect(calls.filter((c) => c.method === "POST")).toHaveLength(1);
  });

  it("REQ-BT-050 a retry in progress says so with its attempt, then leaves when the read passes", async () => {
    let release: () => void = () => undefined;
    let reads = 0;
    installApi({ "GET /api/me": meRoute("olivia"), [ROUTE]: () => (++reads === 1 ? { status: 503 } : { etag: '"v1"', body: batch() }) });
    setRetryWait(() => new Promise<void>((resolve) => (release = resolve)));
    renderApp("/batches/farm-a/b-1");
    expect(await screen.findByRole("status", { name: "Retrying, attempt 2 of 10" })).toBeInTheDocument();
    await act(async () => release());
    expect(await screen.findByRole("heading", { name: "Bovine vaccine" })).toBeInTheDocument();
    expect(screen.queryByRole("status", { name: /Retrying/ })).toBeNull();
  });

  it("REQ-BT-050 a wait from the server says when the next try is", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), [ROUTE]: { status: 429, headers: { "Retry-After": "5" } } });
    setRetryWait((_ms, signal) => new Promise<void>((_resolve, reject) => signal?.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")))));
    renderApp("/batches/farm-a/b-1");
    expect(await screen.findByRole("status", { name: "Retrying, attempt 2 of 10, next try in 5 s" })).toBeInTheDocument();
  });

  it("REQ-BT-050 ten failures end in an error with Try again, and the retry notice gone", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), [ROUTE]: { status: 503 } });
    setRetryWait(async () => undefined);
    renderApp("/batches/farm-a/b-1");
    expect(await screen.findByRole("button", { name: "Try again" })).toBeInTheDocument();
    expect(screen.queryByRole("status", { name: /Retrying/ })).toBeNull();
  });

  it("REQ-BT-050 a refused read shows the usual page, with no retry notice", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia"), [ROUTE]: { status: 404 } });
    renderApp("/batches/farm-a/b-1");
    expect(await screen.findByText("Not found")).toBeInTheDocument();
    expect(calls.filter((c) => c.url.endsWith("/b-1"))).toHaveLength(1);
  });

  it("REQ-BT-050 leaving the page during a retry stops it", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [] }, [ROUTE]: { status: 503 } });
    setRetryWait(undefined);
    renderApp("/batches/farm-a/b-1");
    await screen.findByRole("status", { name: /Retrying/ });
    await userEvent.click(screen.getByRole("link", { name: "Batches" }));
    await waitFor(() => expect(screen.queryByRole("status", { name: /Retrying/ })).toBeNull());
    const reads = calls.filter((c) => c.url.endsWith("/b-1")).length;
    await act(() => new Promise<void>((resolve) => setTimeout(resolve, 500)));
    expect(calls.filter((c) => c.url.endsWith("/b-1"))).toHaveLength(reads);
  });
});

describe("REQ-BT-050 socket reconnects", () => {
  function connect() {
    FakeSocket.reset();
    const socket = new LiveSocket<unknown>({ url: "ws://x", parse: () => null, onData: () => undefined, onState: () => undefined, create: (url) => new FakeSocket(url) });
    socket.start();
    return socket;
  }

  it("REQ-BT-050 the wait is spread by jitter, so many tabs do not reconnect together", () => {
    vi.useFakeTimers();
    vi.spyOn(Math, "random").mockReturnValue(0);
    const socket = connect();
    FakeSocket.all[0]!.drop();
    vi.advanceTimersByTime(99);
    expect(FakeSocket.all).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(FakeSocket.all).toHaveLength(2);
    socket.stop();
  });

  it("REQ-BT-050 it keeps trying past 10 attempts, with the wait capped", () => {
    vi.useFakeTimers();
    vi.spyOn(Math, "random").mockReturnValue(1);
    const socket = connect();
    for (let i = 0; i < 15; i += 1) {
      FakeSocket.all.at(-1)!.drop();
      vi.advanceTimersByTime(30_000);
    }
    expect(FakeSocket.all).toHaveLength(16);
    socket.stop();
  });
});
