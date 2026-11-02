import { act, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { client } from "./client";
import { ApiError, describeProblem } from "./problem";
import { setRetryWait } from "./retry";
import { hopSnapshot } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../mocks/harness";

const FILTERS = { level: "", reason: "", farm: "", batch: "", correlationId: "", from: "", to: "" };
const logsRead = () => client.lineLogs(FILTERS).catch((e: unknown) => e);
const CUT = () => Promise.resolve({ ok: true, status: 200, headers: new Headers(), text: () => Promise.reject(new TypeError("terminated")) });
const NOT_JSON = () => Promise.resolve(new Response("<html>a proxy page</html>", { status: 200 }));
const REFUSED = () => Promise.reject(new TypeError("Failed to fetch"));
const request = (method: string, url: string) => ({ method, url });
const EXPIRED = /session expired|session ended/i;

beforeEach(() => {
  useFakeSockets();
  setRetryWait(async () => undefined);
});
afterEach(() => void vi.unstubAllGlobals());

describe("REQ-BT-050 words say only what the client saw", () => {
  it("REQ-BT-050 nothing came back: the server was not reached", async () => {
    vi.stubGlobal("fetch", REFUSED);
    const view = describeProblem(await logsRead());
    expect([view.title, view.message, view.next]).toEqual(["Cannot reach the server", "The server did not answer.", "Check the connection, then try again."]);
  });

  it.each([
    ["a 200 cut off", CUT],
    ["a 200 that is not JSON", NOT_JSON],
  ])("REQ-BT-050 %s: an answer began, so it is no connection fault", async (_name, answer) => {
    vi.stubGlobal("fetch", answer);
    const view = describeProblem(await logsRead());
    expect(view.title).toBe("No usable answer");
    expect(view.message).toBe("The answer was cut off or could not be read.");
    expect(view.next).toBe("Try again.");
    expect(view.recovery).toBe("retry");
  });

  it.each([502, 503, 504, 405, 413])("REQ-BT-050 a proxy's %i page says nothing about the server", async (status) => {
    vi.stubGlobal("fetch", () => Promise.resolve(new Response("<html>a proxy page</html>", { status })));
    const view = describeProblem(await logsRead());
    expect([view.title, view.message]).toEqual(["The request failed", "The answer gave no reason."]);
  });

  it("REQ-BT-050 a refusal cut off says nothing about the server", async () => {
    vi.stubGlobal("fetch", () => Promise.resolve({ ok: false, status: 500, statusText: "", headers: new Headers(), json: () => Promise.reject(new TypeError("terminated")) }));
    const view = describeProblem(await logsRead());
    expect([view.title, view.message]).toEqual(["The request failed", "The answer gave no reason."]);
  });

  it("REQ-BT-050 a JSON body with neither title nor detail counts as no reason", async () => {
    vi.stubGlobal("fetch", () => Promise.resolve(new Response("{}", { status: 500 })));
    expect(describeProblem(await logsRead()).title).toBe("The request failed");
  });

  it("REQ-BT-050 a title with no detail is the reason shown", async () => {
    vi.stubGlobal("fetch", () => Promise.resolve(new Response(JSON.stringify({ title: "The store is down" }), { status: 500 })));
    const view = describeProblem(await logsRead());
    expect([view.title, view.message]).toEqual(["The server could not finish this", "The store is down"]);
  });

  it("REQ-BT-050 a server's own detail still wins", () => {
    expect(describeProblem(new ApiError(500, "t", "The store is down.")).message).toBe("The store is down.");
  });

  it.each([
    ["the logs", "/line/logs"],
    ["performance data", "/metrics/series"],
    ["sign-in", "/api/auth/dev-login"],
    ["the batch list", "/api/batches"],
  ])("REQ-BT-050 a 404 on %s names no batch", (_name, url) => {
    const view = describeProblem(new ApiError(404, "t", null, { request: request("GET", url) }));
    expect(view.message).toBe("Nothing was found at this address.");
  });

  it.each([
    ["reading it", "GET", "/api/batches/farm-a/b-1"],
    ["moving it", "POST", "/api/batches/farm-a/b-1/advance"],
  ])("REQ-BT-050 a 404 %s still names the batch", (_name, method, url) => {
    const view = describeProblem(new ApiError(404, "t", null, { request: request(method, url) }));
    expect(view.message).toMatch(/This batch does not exist/);
  });

  it("REQ-BT-050 a 404 creating a batch names no batch and still counts as a batch write", () => {
    const view = describeProblem(new ApiError(404, "t", null, { request: request("POST", "/api/batches") }));
    expect(view.message).toBe("Nothing was found at this address.");
    expect(view.impact).toBe("Nothing was saved.");
  });

  it("REQ-BT-050 a refused sign-in says so, not that a session ended", async () => {
    vi.stubGlobal("fetch", () => Promise.resolve(new Response(JSON.stringify({ title: "Unauthorized", detail: "Unknown user." }), { status: 401 })));
    const view = describeProblem(await client.devLogin("nobody").catch((e: unknown) => e));
    expect(view.title).toBe("Sign-in refused");
    expect(view.message).toBe("Unknown user.");
    expect(view.recovery).toBe("retry");
  });

  it("REQ-BT-050 a 401 elsewhere still says the session ended", () => {
    const view = describeProblem(new ApiError(401, "t", null, { request: request("GET", "/api/batches") }));
    expect([view.title, view.recovery]).toEqual(["Session ended", "sign-in"]);
  });

  it("REQ-BT-050 a fault in the page names no server and no connection", () => {
    const view = describeProblem(new Error("boom"));
    expect(view.title).toBe("This page hit an error");
    expect(`${view.title} ${view.message} ${view.next}`).not.toMatch(/server|connection/i);
  });
});

describe("REQ-BT-050 outside the problem view", () => {
  it.each([
    ["a 500 with no reason", () => Promise.resolve(new Response("{}", { status: 500 })), "The request failed"],
    ["a 200 cut off", CUT, "No usable answer"],
  ])("REQ-BT-050 a session check that got %s does not say the server was out of reach", async (_name, answer, title) => {
    installApi({});
    vi.stubGlobal("fetch", answer);
    renderApp("/batches");
    expect(await screen.findByText(title)).toBeInTheDocument();
    expect(screen.queryByText("Cannot reach the server")).not.toBeInTheDocument();
  });

  it("REQ-BT-050 a refused sign-in stays on the page and says why", async () => {
    installApi({
      "GET /api/me": { status: 401, body: { title: "Unauthorized" } },
      "POST /api/auth/dev-login": { status: 401, body: { title: "Unauthorized", detail: "Unknown user." } },
    });
    renderApp("/signin");
    await userEvent.click(await screen.findByRole("button", { name: /Continue as ian/ }));
    expect(await screen.findByText("Sign-in refused")).toBeInTheDocument();
    expect(screen.getByText("Unknown user.")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Sign in to Batch Tracker" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Try again" })).toBeInTheDocument();
    expect(screen.queryByText(EXPIRED)).not.toBeInTheDocument();
  });

  it("REQ-BT-050 sign-in switched off offers no way back to batches", async () => {
    installApi({ "GET /api/me": { status: 401, body: { title: "Unauthorized" } }, "POST /api/auth/dev-login": { status: 404 } });
    renderApp("/signin");
    await userEvent.click(await screen.findByRole("button", { name: /Continue as ian/ }));
    expect(await screen.findByText("Not found")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /batches/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /batches/i })).not.toBeInTheDocument();
    expect(screen.queryByText(/batch list/)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Try again" })).toBeInTheDocument();
  });

  it("REQ-BT-050 alerts unknown after a stream that did answer says it was lost", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    renderApp("/alerts");
    await screen.findByRole("heading", { name: "Alerts" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.send(hopSnapshot({ alerts: [] })));
    await screen.findByText("No open alerts");
    act(() => socket.drop());
    expect(await screen.findByText(/Live updates are lost/)).toBeInTheDocument();
    expect(screen.queryByText(/did not answer/)).not.toBeInTheDocument();
  });
});
