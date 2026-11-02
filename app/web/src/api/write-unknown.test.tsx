import { vi } from "vitest";
import { useFakeSockets } from "../mocks/harness";
import { client } from "./client";
import { ApiError, describeProblem } from "./problem";
import { setRetryWait } from "./retry";

/** Sign-in and sign-out only: a repeat of either leaves batches and the line as they were. */
const SAFE_TO_REPEAT = ["devLogin", "logout"];

type Lost = { method: string; error: ApiError };

/** Walks the whole client, so a write added later is covered without touching this file. */
async function everyRequest(answer: () => Promise<unknown>): Promise<Record<string, Lost>> {
  const seen: Record<string, Lost> = {};
  for (const [name, call] of Object.entries(client)) {
    let method = "";
    vi.stubGlobal("fetch", (_: string, init: RequestInit) => {
      method = init.method ?? "GET";
      return answer();
    });
    const error = (await (call as (...args: unknown[]) => Promise<unknown>)(...(name === "me" ? [] : ["farm-a"])).catch((e: unknown) => e)) as ApiError;
    seen[name] = { method, error };
  }
  return seen;
}

const LOST: [string, () => Promise<unknown>][] = [
  ["no answer at all", () => Promise.reject(new TypeError("Failed to fetch"))],
  ["a 502 from the ingress", () => Promise.resolve(new Response("<html>", { status: 502 }))],
  ["a 504 from the ingress", () => Promise.resolve(new Response("<html>", { status: 504 }))],
  ["a body cut off", () => Promise.resolve({ ok: true, status: 200, headers: new Headers(), text: () => Promise.reject(new TypeError("terminated")) })],
  ["a 200 that is not JSON", () => Promise.resolve(new Response("<html>", { status: 200 }))],
];

beforeEach(() => {
  useFakeSockets();
  setRetryWait(async () => undefined);
});
afterEach(() => void vi.unstubAllGlobals());

describe("REQ-BT-050 what a lost write says", () => {
  const advance = () => client.advance("farm-a", "b-1", "Dose2", '"v1"').catch((e: unknown) => e);

  it.each(LOST)("REQ-BT-050 %s: says only that no usable answer came", async (_name, answer) => {
    vi.stubGlobal("fetch", answer);
    expect(describeProblem(await advance()).message).toBe("No usable answer came back.");
  });

  it.each(LOST)("REQ-BT-050 %s: a batch write may have saved", async (_name, answer) => {
    vi.stubGlobal("fetch", answer);
    expect(describeProblem(await advance()).impact).toBe("This may have saved.");
  });

  it("REQ-BT-050 a batch write offers a reload of the batch", async () => {
    vi.stubGlobal("fetch", LOST[0]![1]);
    expect(describeProblem(await advance()).recovery).toBe("reload-batch");
  });

  it.each([
    ["creating a batch", () => client.createBatch("farm-a", "Bovine vaccine")],
    ["setting up a failed delivery", () => client.failNext("farm-a", "b-1")],
  ])("REQ-BT-050 %s points at the batch to check", async (_name, call) => {
    vi.stubGlobal("fetch", LOST[0]![1]);
    expect(describeProblem(await call().catch((e: unknown) => e)).recovery).toBe("reload-batch");
  });

  it.each([
    ["the nightly run", () => client.runCompliance()],
    ["starting the line", () => client.lineStart(5)],
    ["stopping the line", () => client.lineStop()],
  ])("REQ-BT-050 %s has no batch to reload", async (_name, call) => {
    vi.stubGlobal("fetch", LOST[0]![1]);
    expect(describeProblem(await call().catch((e: unknown) => e)).recovery).toBe("check-state");
  });
});

describe("REQ-BT-050 a write nobody answered", () => {
  it.each(LOST)("REQ-BT-050 %s: every write says its result is unknown, by default", async (_name, answer) => {
    const seen = await everyRequest(answer);
    const writes = Object.entries(seen).filter(([name, { method }]) => method !== "GET" && !SAFE_TO_REPEAT.includes(name));
    expect(writes.map(([name]) => name).sort()).toEqual(["advance", "createBatch", "failNext", "inspect", "lineStart", "lineStop", "replay", "runCompliance"]);
    for (const [name, { error }] of writes) expect([name, describeProblem(error).title]).toEqual([name, "The result is unknown"]);
  });

  it.each(LOST)("REQ-BT-050 %s: a read and a sign-in or sign-out keep plain words", async (_name, answer) => {
    const seen = await everyRequest(answer);
    const plain = Object.entries(seen).filter(([name, { method }]) => method === "GET" || SAFE_TO_REPEAT.includes(name));
    expect(plain.length).toBeGreaterThan(SAFE_TO_REPEAT.length);
    for (const [name, { error }] of plain) expect([name, describeProblem(error).title]).not.toEqual([name, "The result is unknown"]);
  });
});
