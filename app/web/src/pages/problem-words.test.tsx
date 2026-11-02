import { vi } from "vitest";
import { client } from "../api/client";
import { describeProblem } from "../api/problem";
import { installApi, useFakeSockets } from "../mocks/harness";

const BATCH = "GET /api/batches/farm-a/b-1";
const ADVANCE = "POST /api/batches/farm-a/b-1/advance";
const SAID = { where: "Where from the server", impact: "Impact from the server", next: "Next from the server" };

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

describe("REQ-BT-053 plain words for a problem", () => {
  const failure = (run: () => Promise<unknown>) => run().catch((e: unknown) => e);

  it("REQ-BT-053 a refused write says where, and that nothing was saved", async () => {
    installApi({ [ADVANCE]: { status: 412 } });
    const view = describeProblem(await failure(() => client.advance("farm-a", "b-1", "Dose2", '"v1"')));
    expect(view.where).toBe("Moving batch b-1 on farm-a");
    expect(view.impact).toBe("Nothing was saved.");
    expect(view.next).toMatch(/Reload the batch/);
  });

  it("REQ-BT-053 a failed read says it changed nothing", async () => {
    installApi({ [BATCH]: { status: 404 } });
    const view = describeProblem(await failure(() => client.getBatch("farm-a", "b-1")));
    expect(view.where).toBe("Reading batch b-1 on farm-a");
    expect(view.impact).toBe("Nothing changed: this was only a read.");
  });

  it("REQ-BT-053 a lost answer says the batch may have changed", async () => {
    installApi({ [ADVANCE]: { status: 504 } });
    const view = describeProblem(await failure(() => client.advance("farm-a", "b-1", "Dose2", '"v1"')));
    expect(view.impact).toMatch(/may have saved/);
  });

  it("REQ-BT-053 failed work with no batch shows the impact the server sent", async () => {
    installApi({ "POST /line": { status: 500, body: { title: "Failed", impact: "Impact from the server" } } });
    const view = describeProblem(await failure(() => client.lineStart(5)));
    expect(view.where).toBe("Starting the line or changing its rate");
    expect(view.impact).toBe("Impact from the server");
  });

  it("REQ-BT-053 what the server said comes first, on every field", async () => {
    installApi({ [ADVANCE]: { status: 412, body: { ...SAID, title: "Refused" } } });
    const view = describeProblem(await failure(() => client.advance("farm-a", "b-1", "Dose2", '"v1"')));
    expect(view).toMatchObject({ where: SAID.where, impact: SAID.impact, next: SAID.next });
  });

  it("REQ-BT-053 a write of unknown result still shows the server's words when one answered", async () => {
    installApi({ [ADVANCE]: { status: 504, body: { ...SAID, title: "Timed out" } } });
    const view = describeProblem(await failure(() => client.advance("farm-a", "b-1", "Dose2", '"v1"')));
    expect(view.title).toBe("The result is unknown");
    expect(view).toMatchObject({ where: SAID.where, impact: SAID.impact, next: SAID.next });
  });

  it.each([
    ["empty text", ""],
    ["only spaces", "   "],
    ["a value that is not text", 7],
    ["nothing", null],
  ])("REQ-BT-053 %s from the server falls back to the web's own words", async (_name, value) => {
    installApi({ [ADVANCE]: { status: 412, body: { title: "Refused", where: value, impact: value, next: value } } });
    const view = describeProblem(await failure(() => client.advance("farm-a", "b-1", "Dose2", '"v1"')));
    expect(view.where).toBe("Moving batch b-1 on farm-a");
    expect(view.impact).toBe("Nothing was saved.");
    expect(view.next).toMatch(/Reload the batch/);
  });

  it("REQ-BT-053 work with no batch and no answer from a server says its effect is not known", async () => {
    installApi({ "POST /line": () => Promise.reject(new TypeError("failed to fetch")) });
    const view = describeProblem(await failure(() => client.lineStart(5)));
    expect(view.impact).toBe("The server did not say what this did to any batch.");
  });

  it("REQ-BT-053 a read with no batch and no answer from a server changed nothing", async () => {
    installApi({ "GET /line/logs": () => Promise.reject(new TypeError("failed to fetch")) });
    const view = describeProblem(await failure(() => client.lineLogs({ level: "", reason: "", farm: "", batch: "", correlationId: "", from: "", to: "" })));
    expect(view.impact).toBe("Nothing changed: this was only a read.");
  });

  it("REQ-BT-053 an error with no request behind it still has a message and no invented rows", () => {
    const view = describeProblem(new Error("boom"));
    expect(view.message.length).toBeGreaterThan(0);
    expect(view.where).toBeNull();
    expect(view.impact).toBeNull();
  });
});

describe("REQ-BT-050 the wait a 429 asks for", () => {
  const limited = (next: string, status = 429, retryAfter: string | null = "120") => ({
    status,
    headers: retryAfter ? { "Retry-After": retryAfter } : undefined,
    body: {
      type: "https://tools.ietf.org/html/rfc9110#section-15.5.29",
      title: "Too Many Requests",
      status,
      detail: "Words the server chose.",
      where: "Where from the server",
      impact: "Impact from the server",
      next,
      correlationId: "corr-429",
    },
  });
  const failure = (run: () => Promise<unknown>) => run().catch((e: unknown) => e);
  const nextFor = async (reply: ReturnType<typeof limited>) => {
    installApi({ [ADVANCE]: reply });
    return describeProblem(await client.advance("farm-a", "b-1", "Dose2", '"v1"').catch((e: unknown) => e)).next;
  };

  it("REQ-BT-050 keeps the server's words and adds the seconds they leave out", async () => {
    expect(await nextFor(limited("Wait, then send it again."))).toBe("Wait, then send it again. Try again in 120 s.");
  });

  it("REQ-BT-050 adds nothing when the server's words already hold the seconds", async () => {
    expect(await nextFor(limited("Wait 120 s, then send it again."))).toBe("Wait 120 s, then send it again.");
  });

  it("REQ-BT-050 another number in the words is not the wait", async () => {
    expect(await nextFor(limited("Code 1205, send it again."))).toBe("Code 1205, send it again. Try again in 120 s.");
  });

  it("REQ-BT-050 with no Retry-After there is no wait to add", async () => {
    expect(await nextFor(limited("Wait, then send it again.", 429, null))).toBe("Wait, then send it again.");
  });

  it.each([
    ["seconds with no space", "Wait 120s."],
    ["the word seconds", "Wait 120 seconds."],
  ])("REQ-BT-050 %s already hold the wait", async (_name, next) => {
    expect(await nextFor(limited(next))).toBe(next);
  });

  it("REQ-BT-050 the same number meaning something else is not the wait", async () => {
    expect(await nextFor(limited("Limit is 120 per minute."))).toBe("Limit is 120 per minute. Try again in 120 s.");
  });

  it.each([
    ["zero", "0"],
    ["a date already past", "Wed, 21 Oct 2015 07:28:00 GMT"],
  ])("REQ-BT-050 a wait of %s is no wait: the server's words stand alone", async (_name, header) => {
    expect(await nextFor(limited("Wait, then send it again.", 429, header))).toBe("Wait, then send it again.");
  });

  it.each([
    ["zero", "0"],
    ["a date already past", "Wed, 21 Oct 2015 07:28:00 GMT"],
  ])("REQ-BT-050 with no server words, a wait of %s never reads Try again in 0 s", async (_name, header) => {
    installApi({ [ADVANCE]: { status: 429, headers: { "Retry-After": header } } });
    const view = describeProblem(await failure(() => client.advance("farm-a", "b-1", "Dose2", '"v1"')));
    expect(view.next).toBe("Try again in a moment.");
  });

  it("REQ-BT-050 only a 429 gets the wait added", async () => {
    expect(await nextFor(limited("Try again in a moment.", 503))).toBe("Try again in a moment.");
  });
});
