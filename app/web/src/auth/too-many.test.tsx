import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { client } from "../api/client";
import { ApiError } from "../api/problem";
import { nextWait, setRetryWait } from "../api/retry";
import { USERS, batch, hopSnapshot } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Reply } from "../mocks/harness";

const SIGN_IN = "Sign in to Batch Tracker";
const refused = (seconds: number): Reply => ({
  status: 429,
  headers: { "Retry-After": String(seconds) },
  body: { title: "Too many requests", detail: "Too many failed requests. Wait before trying again." },
});
const press = async (user: string) => userEvent.click(await screen.findByRole("button", { name: new RegExp(`Continue as ${user}`) }));

beforeEach(() => {
  useFakeSockets();
  FakeSocket.greet("/ws/hops", hopSnapshot());
  setRetryWait(async () => undefined);
});
afterEach(() => {
  setRetryWait(undefined);
  vi.unstubAllGlobals();
});

describe("REQ-BT-049 how long a 429 waits", () => {
  it.each([
    [1, 1000],
    [30, 30_000],
  ])("REQ-BT-049 %i s is short enough to wait for", (seconds, ms) => {
    expect(nextWait(new ApiError(429, "t", null, { retryAfterMs: seconds * 1000 }), 1)).toBe(ms);
  });

  it.each([31, 900])("REQ-BT-049 %i s ends the wait at once, with no spinner", (seconds) => {
    expect(nextWait(new ApiError(429, "t", null, { retryAfterMs: seconds * 1000 }), 1)).toBeNull();
  });

  it("REQ-BT-049 a read refused for the longest wait asks once and shows the page the wait and the words", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": refused(900) });
    renderApp("/batches");
    expect(await screen.findByText("Too many failed requests. Wait before trying again.")).toBeInTheDocument();
    expect(screen.getByText("Try again in 900 s.")).toBeInTheDocument();
    expect(calls.filter((call) => call.url.includes("/api/batches"))).toHaveLength(1);
    expect(screen.queryByLabelText(/Retrying/)).not.toBeInTheDocument();
  });
});

describe("REQ-BT-049 what never waits", () => {
  it.each([
    ["signing in", "POST /api/auth/dev-login", () => client.devLogin("ada")],
    ["signing out", "POST /api/auth/logout", () => client.logout()],
  ])("REQ-BT-049 %s has a method, so even a short wait is not waited out", async (_name, route, call) => {
    const calls = installApi({ [route]: refused(5) });
    await expect(call()).rejects.toMatchObject({ status: 429 });
    expect(calls).toHaveLength(1);
  });
});

describe("REQ-BT-049 the session check refused", () => {
  it.each([5, 900])("REQ-BT-049 a %i s refusal reads as not signed in, at once, with no failed restore", async (seconds) => {
    const calls = installApi({ "GET /api/me": refused(seconds) });
    renderApp("/batches");
    expect(await screen.findByRole("heading", { name: SIGN_IN })).toBeInTheDocument();
    expect(calls.filter((call) => call.url.includes("/api/session"))).toHaveLength(1);
    expect(screen.queryByText("Your session could not be checked.")).not.toBeInTheDocument();
    expect(screen.queryByText("Your session ended")).not.toBeInTheDocument();
  });

  it("REQ-BT-049 any other failed check still reads as failed", async () => {
    installApi({ "GET /api/me": { status: 500, body: { title: "Failed" } } });
    renderApp("/batches");
    expect(await screen.findByText("Your session could not be checked.")).toBeInTheDocument();
  });
});

describe("REQ-BT-049 the sign-in page while refused", () => {
  const signedOut: Reply = { status: 401, body: { title: "Unauthorized" } };

  it("REQ-BT-049 a right credential still passes", async () => {
    installApi({ "GET /api/me": refused(900), "POST /api/auth/dev-login": { body: USERS.ada }, "GET /api/batches": { body: [batch()] } });
    renderApp("/signin");
    await press("ada");
    await waitFor(() => expect(screen.queryByRole("heading", { name: SIGN_IN })).not.toBeInTheDocument());
  });

  it("REQ-BT-049 a wrong one shows the wait, is not repeated, and the page still works", async () => {
    let wrong = true;
    const calls = installApi({
      "GET /api/me": signedOut,
      "POST /api/auth/dev-login": () => (wrong ? refused(900) : { body: USERS.ada }),
      "GET /api/batches": { body: [batch()] },
    });
    renderApp("/signin");
    await press("ada");
    expect(await screen.findByText("Too many requests")).toBeInTheDocument();
    expect(screen.getByText("Try again in 900 s.")).toBeInTheDocument();
    expect(calls.filter((call) => call.url.includes("dev-login"))).toHaveLength(1);
    wrong = false;
    await press("ada");
    await waitFor(() => expect(screen.queryByRole("heading", { name: SIGN_IN })).not.toBeInTheDocument());
  });
});
