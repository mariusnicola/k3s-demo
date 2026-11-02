import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { USERS, batch, hopSnapshot } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../mocks/harness";

const SIGNED_OUT = { status: 401, body: { title: "Unauthorized" } };

beforeEach(() => {
  useFakeSockets();
  sessionStorage.clear();
  localStorage.clear();
});
afterEach(() => void vi.unstubAllGlobals());

describe("the session read at load", () => {
  it("REQ-BT-001 a signed-out load gets a 200 from /api/session and never calls /api/me, so the browser logs no 401", async () => {
    const calls = installApi({ "GET /api/session": { body: { me: null } }, "GET /api/me": () => { throw new Error("a load must not call /api/me"); } });
    renderApp("/");
    expect(await screen.findByRole("button", { name: /Continue as ada/ })).toBeInTheDocument();
    expect(calls.filter((call) => call.url.includes("/api/session"))).toHaveLength(1);
    expect(calls.filter((call) => call.url.includes("/api/me"))).toEqual([]);
  });

  it("REQ-BT-001 a signed-in load takes the user from the session answer", async () => {
    const calls = installApi({ "GET /api/session": { body: { me: USERS.olivia } }, "GET /api/batches": { body: [] } });
    renderApp("/batches");
    await waitFor(() => expect(calls.some((call) => call.url.includes("/api/batches"))).toBe(true));
    expect(screen.queryByRole("button", { name: /Continue as/ })).not.toBeInTheDocument();
  });
});

describe("session lives in a cookie the page cannot read", () => {
  it("REQ-BT-047 TM-1931 after sign in nothing readable by script holds the session", async () => {
    let signedIn = false;
    const calls = installApi({
      "POST /api/auth/dev-login": () => {
        signedIn = true;
        return { body: USERS.olivia };
      },
      "GET /api/me": () => (signedIn ? meRoute("olivia") : SIGNED_OUT),
      "GET /api/batches": { body: [batch()] },
    });
    renderApp("/signin");
    await userEvent.click(await screen.findByRole("button", { name: /Continue as olivia/ }));
    await screen.findByRole("heading", { name: "Dashboard" });
    await screen.findByRole("link", { name: "Bovine vaccine" });
    expect(Object.keys(sessionStorage)).toEqual([]);
    expect(Object.keys(localStorage).filter((k) => /token|session/i.test(k))).toEqual([]);
    expect(calls.filter((c) => "Authorization" in c.headers)).toEqual([]);
  });

  it("REQ-BT-047 TM-1934 a token left from before the change is removed and never sent", async () => {
    sessionStorage.setItem("bt.token", "old");
    const calls = installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [] } });
    renderApp("/batches");
    await screen.findByRole("heading", { name: "Batches" });
    expect(sessionStorage.getItem("bt.token")).toBeNull();
    expect(calls.filter((c) => "Authorization" in c.headers)).toEqual([]);
  });

  it("REQ-BT-047 a reload with the cookie restores the session from /me alone", async () => {
    installApi({ "GET /api/me": meRoute("ian"), "GET /api/batches": { body: [] } });
    renderApp("/batches");
    expect(await screen.findByRole("heading", { name: "Batches" })).toBeInTheDocument();
    expect(screen.getByText("ian")).toBeInTheDocument();
  });

  it("REQ-BT-047 no cookie at load is plain sign in, with no session ended warning", async () => {
    installApi({ "GET /api/me": SIGNED_OUT });
    renderApp("/alerts");
    expect(await screen.findByRole("button", { name: /Continue as olivia/ })).toBeInTheDocument();
    expect(screen.queryByText("Your session ended")).toBeNull();
  });

  it("REQ-BT-047 TM-1932 a 401 on a page goes to sign in, says why, and returns to the same address", async () => {
    let expired = false;
    installApi({
      "GET /api/me": () => (expired ? SIGNED_OUT : meRoute("olivia")),
      "POST /api/auth/dev-login": () => {
        expired = false;
        return { body: USERS.olivia };
      },
      "GET /api/batches/farm-a/b-1": () => (expired ? SIGNED_OUT : { etag: '"v1"', body: batch() }),
      "POST /api/batches/farm-a/b-1/advance": () => {
        expired = true;
        return SIGNED_OUT;
      },
    });
    renderApp("/batches/farm-a/b-1");
    await userEvent.click(await screen.findByRole("button", { name: "Move to Dose 2" }));
    expect(await screen.findByText("Your session ended")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /Continue as olivia/ }));
    expect(await screen.findByRole("heading", { name: "Bovine vaccine" })).toBeInTheDocument();
  });

  it("REQ-BT-047 switch user posts logout once and closes the socket", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia"), "POST /api/auth/logout": { status: 204 } });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    renderApp("/alerts");
    await screen.findByText("No open alerts");
    const socket = FakeSocket.find("/ws/hops");
    await userEvent.click(screen.getByRole("button", { name: /Switch user/ }));
    await waitFor(() => expect(calls.filter((c) => c.method === "POST" && c.url.endsWith("/api/auth/logout"))).toHaveLength(1));
    expect(socket.closed).toBe(true);
    expect(await screen.findByRole("button", { name: /Continue as olivia/ })).toBeInTheDocument();
  });

  it("REQ-BT-047 a logout the server did not take says this browser may still be signed in, and can be repeated", async () => {
    let reply: { status: number } = { status: 500 };
    const calls = installApi({ "GET /api/me": meRoute("olivia"), "POST /api/auth/logout": () => reply });
    renderApp("/alerts");
    await userEvent.click(await screen.findByRole("button", { name: /Switch user/ }));
    expect(await screen.findByText("Sign out did not finish")).toBeInTheDocument();
    expect(screen.getByText(/may still be signed in/)).toBeInTheDocument();
    reply = { status: 204 };
    await userEvent.click(screen.getByRole("button", { name: "Sign out again" }));
    await waitFor(() => expect(screen.queryByText("Sign out did not finish")).toBeNull());
    expect(calls.filter((c) => c.url.endsWith("/api/auth/logout"))).toHaveLength(2);
  });

  it("REQ-BT-047 a logout that fails on the network says so too", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    renderApp("/alerts");
    const button = await screen.findByRole("button", { name: /Switch user/ });
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("down")));
    await userEvent.click(button);
    expect(await screen.findByText("Sign out did not finish")).toBeInTheDocument();
  });

  it("REQ-BT-047 a logout answered 401 was already signed out, so nothing is said", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "POST /api/auth/logout": SIGNED_OUT });
    renderApp("/alerts");
    await userEvent.click(await screen.findByRole("button", { name: /Switch user/ }));
    await screen.findByRole("button", { name: /Continue as olivia/ });
    await act(async () => undefined);
    expect(screen.queryByText("Sign out did not finish")).toBeNull();
  });

  it("REQ-BT-047 signing in clears a failed sign out notice", async () => {
    let logouts = 0;
    installApi({ "GET /api/me": meRoute("olivia"), "POST /api/auth/logout": () => ({ status: ++logouts === 1 ? 500 : 204 }), "POST /api/auth/dev-login": { body: USERS.ian } });
    renderApp("/alerts");
    await userEvent.click(await screen.findByRole("button", { name: /Switch user/ }));
    await screen.findByText("Sign out did not finish");
    await userEvent.click(screen.getByRole("button", { name: /Continue as ian/ }));
    await screen.findByRole("heading", { name: "Alerts" });
    await userEvent.click(screen.getByRole("button", { name: /Switch user/ }));
    await waitFor(() => expect(logouts).toBe(2));
    await screen.findByRole("button", { name: /Continue as ian/ });
    await act(async () => undefined);
    expect(screen.queryByText("Sign out did not finish")).toBeNull();
  });

  it("REQ-BT-047 a session that expires after a failed sign out does not show the old notice", async () => {
    let expired = false;
    installApi({
      "GET /api/me": meRoute("olivia"),
      "POST /api/auth/logout": { status: 500 },
      "POST /api/auth/dev-login": { body: USERS.olivia },
      "GET /api/batches/farm-a/b-1": () => (expired ? SIGNED_OUT : { etag: '"v1"', body: batch() }),
      "POST /api/batches/farm-a/b-1/advance": () => {
        expired = true;
        return SIGNED_OUT;
      },
    });
    renderApp("/batches/farm-a/b-1");
    await userEvent.click(await screen.findByRole("button", { name: /Switch user/ }));
    await screen.findByText("Sign out did not finish");
    await userEvent.click(screen.getByRole("button", { name: /Continue as olivia/ }));
    await userEvent.click(await screen.findByRole("button", { name: "Move to Dose 2" }));
    expect(await screen.findByText("Your session ended")).toBeInTheDocument();
    expect(screen.queryByText("Sign out did not finish")).toBeNull();
  });

  it("REQ-BT-047 TM-1933 no socket address holds a token", async () => {
    installApi({ "GET /api/me": meRoute("ada") });
    renderApp("/dashboard");
    await screen.findByRole("heading", { name: "Production line" });
    act(() => undefined);
    expect(FakeSocket.all.filter((s) => /token/i.test(s.url))).toEqual([]);
  });
});

describe("a crafted next link", () => {
  it.each([
    ["TM-2295 backslash", "/%5Cexample.invalid/x"],
    ["TM-2297 tab", "/%09/example.invalid/x"],
    ["control character", "/%01/example.invalid/x"],
    ["newline", "/%0A/example.invalid/x"],
  ])("REQ-BT-047 %s lands on the first page, not a blank one", async (_name, next) => {
    installApi({
      "GET /api/me": SIGNED_OUT,
      "POST /api/auth/dev-login": { body: USERS.olivia },
      "GET /api/batches": { body: [] },
    });
    renderApp(`/signin?next=${next}`);
    await userEvent.click(await screen.findByRole("button", { name: /Continue as olivia/ }));
    expect(await screen.findByRole("heading", { name: "Dashboard" })).toBeInTheDocument();
  });

  it("REQ-BT-047 a next the new user cannot open lands on the Dashboard and says why", async () => {
    installApi({
      "GET /api/me": SIGNED_OUT,
      "POST /api/auth/dev-login": { body: USERS.olivia },
      "GET /api/batches": { body: [] },
    });
    renderApp(`/signin?next=${encodeURIComponent("/logs")}`);
    await userEvent.click(await screen.findByRole("button", { name: /Continue as olivia/ }));
    expect(await screen.findByRole("heading", { name: "Dashboard" })).toBeInTheDocument();
    expect(screen.getByText("Logs is open to the Admin role. You are signed in as olivia.")).toBeInTheDocument();
  });

  it("REQ-BT-047 a normal next still returns to its page", async () => {
    installApi({
      "GET /api/me": SIGNED_OUT,
      "POST /api/auth/dev-login": { body: USERS.olivia },
      "GET /api/batches": { body: [] },
    });
    renderApp(`/signin?next=${encodeURIComponent("/alerts?x=1")}`);
    await userEvent.click(await screen.findByRole("button", { name: /Continue as olivia/ }));
    expect(await screen.findByRole("heading", { name: "Alerts" })).toBeInTheDocument();
  });
});
