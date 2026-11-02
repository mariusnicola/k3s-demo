import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { MONITOR_WS } from "../config";
import { adminFramesBeforeReport, visitorMonitorFrames } from "../mocks/fixtures";
import { FakeSocket, meRoute, renderApp, type Reply } from "../mocks/harness";
import {
  CHANGE,
  OPERATION,
  SIGN_IN,
  UNKNOWN,
  adminDashboard,
  dropCookie,
  frames,
  fromServer,
  hasCookie,
  monitor,
  openSockets,
  pastStale,
  reopened,
  server,
  signIn,
  signOut,
  useCookieSockets,
} from "../mocks/monitorSession";

beforeEach(() => useCookieSockets());
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("B-046 the cookie outlives the sign-out press", () => {
  it("B-046 no socket is open until logout answers, so the old cookie makes no admin socket", async () => {
    let answer = () => undefined as void;
    const logout = () =>
      new Promise<Reply>((resolve) => {
        answer = () => {
          dropCookie();
          resolve({ status: 204 });
        };
      });
    await adminDashboard({ "POST /api/auth/logout": logout });
    const first = monitor();
    await act(async () => first.open());
    await frames(...fromServer());
    await signOut();
    expect(first.closed).toBe(true);
    expect(openSockets()).toEqual([]);
    await act(async () => answer());
    await reopened();
    await act(async () => monitor().open());
    await frames(...fromServer());
    expect(hasCookie()).toBe(false);
    expect(screen.queryByText("cosmos")).not.toBeInTheDocument();
  });

  it("B-052 a logout still on its way reads as unknown, not as no cluster work", async () => {
    let answer = () => undefined as void;
    await adminDashboard({ "POST /api/auth/logout": () => new Promise<Reply>((resolve) => (answer = () => resolve({ status: 204 }))) });
    await signOut();
    expect(openSockets()).toEqual([]);
    expect(screen.getByText(UNKNOWN)).toBeInTheDocument();
    expect(screen.getByText(/monitor stays closed until sign out finishes/)).toBeInTheDocument();
    await act(async () => answer());
    await reopened();
    await waitFor(() => expect(screen.queryByText(UNKNOWN)).not.toBeInTheDocument());
  });

  it("B-046 a failed logout stays on no socket, says so, and a retry that succeeds opens one", async () => {
    let refuse = true;
    const logout = () => {
      if (refuse) return { status: 500, body: { title: "Failed" } };
      dropCookie();
      return { status: 204 };
    };
    await adminDashboard({ "POST /api/auth/logout": logout });
    await signOut();
    await screen.findByText("Sign out did not finish");
    expect(hasCookie()).toBe(true);
    expect(openSockets()).toEqual([]);
    expect(screen.getByText(UNKNOWN)).toBeInTheDocument();
    expect(screen.getByText(/monitor stays closed until sign out finishes/)).toBeInTheDocument();
    refuse = false;
    await userEvent.click(screen.getByRole("button", { name: "Sign out again" }));
    await reopened();
    await waitFor(() => expect(screen.queryByText(UNKNOWN)).not.toBeInTheDocument());
  });

  it("B-046 signing in after a failed logout opens the socket", async () => {
    await adminDashboard({ "POST /api/auth/logout": { status: 500, body: { title: "Failed" } } });
    await signOut();
    await screen.findByText("Sign out did not finish");
    await signIn();
    await reopened();
  });

  it("B-046 signing in clears what a visitor was shown before", async () => {
    server(null);
    renderApp("/signin");
    await screen.findByRole("heading", { name: SIGN_IN });
    await act(async () => monitor().open());
    await frames(...visitorMonitorFrames([OPERATION]));
    await screen.findByText("Deploy the app");
    await signIn();
    await waitFor(() => expect(screen.queryByText("Deploy the app")).not.toBeInTheDocument());
  });

  it("B-046 a failed restore that recovers opens a new socket", async () => {
    let down = true;
    server(null, { "GET /api/me": () => (down ? { status: 500, body: { title: "Failed" } } : meRoute("ada")) });
    renderApp("/batches");
    await screen.findByText("Your session could not be checked.");
    const first = monitor();
    down = false;
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    await waitFor(() => expect(first.closed).toBe(true));
    await reopened();
  });

  it("B-046 Go to sign in after a failed restore closes the admin socket from the load", async () => {
    server("ada", { "GET /api/me": { status: 500, body: { title: "Failed" } } });
    renderApp("/batches");
    await screen.findByText("Your session could not be checked.");
    const first = monitor();
    await act(async () => first.open());
    await frames(...fromServer());
    await userEvent.click(screen.getByRole("button", { name: "Go to sign in" }));
    await screen.findByRole("heading", { name: SIGN_IN });
    expect(first.closed).toBe(true);
    await reopened();
    expect(monitor()).not.toBe(first);
  });

  describe("B-052 a sign-in that lands before the page's own session check", () => {
    let answerMe: (reply: Reply) => void;
    const slowMe = () => new Promise<Reply>((resolve) => (answerMe = resolve));

    async function signInFirst() {
      server(null, { "GET /api/me": slowMe });
      renderApp("/signin");
      await screen.findByRole("heading", { name: SIGN_IN });
      const first = monitor();
      await signIn();
      await waitFor(() => expect(screen.queryByRole("heading", { name: SIGN_IN })).not.toBeInTheDocument());
      return first;
    }

    it("B-052 turns the socket, so its handshake carries the new cookie", async () => {
      const first = await signInFirst();
      expect(first.closed).toBe(true);
      expect(hasCookie()).toBe(true);
      expect(fromServer()).toHaveLength(3);
    });

    it("B-052 a late 401 from that check does not end the new session", async () => {
      await signInFirst();
      await act(async () => answerMe({ status: 401, body: { title: "Unauthorized" } }));
      expect(screen.queryByRole("heading", { name: SIGN_IN })).not.toBeInTheDocument();
      expect(screen.queryByText("Your session ended")).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: /signed in as ada/ })).toBeInTheDocument();
    });

    it("B-052 a late answer naming someone else does not replace the new user", async () => {
      await signInFirst();
      await act(async () => answerMe(meRoute("viewer")));
      expect(screen.getByRole("button", { name: /signed in as ada/ })).toBeInTheDocument();
    });

    it("B-054 a real 401 from another call ends the session while that check is still open", async () => {
      server(null, { "GET /api/me": slowMe, "GET /api/batches": { status: 401, body: { title: "Unauthorized" } } });
      renderApp("/signin?next=%2Fbatches");
      await signIn();
      expect(await screen.findByText("Your session ended")).toBeInTheDocument();
      expect(screen.getByRole("heading", { name: SIGN_IN })).toBeInTheDocument();
    });

    it("B-054 a socket's 4401 ends the session while that check is still open", async () => {
      await signInFirst();
      await act(async () => FakeSocket.find("/ws/hops").drop(4401));
      expect(await screen.findByRole("heading", { name: SIGN_IN })).toBeInTheDocument();
      expect(screen.getByText("Your session ended")).toBeInTheDocument();
    });

    it("B-054 the monitor socket's 4401 ends the session while that check is still open", async () => {
      await signInFirst();
      const monitor = FakeSocket.all.filter((socket) => socket.url === MONITOR_WS && !socket.closed).at(-1);
      await act(async () => monitor?.drop(4401));
      expect(await screen.findByRole("heading", { name: SIGN_IN })).toBeInTheDocument();
      expect(screen.getByText("Your session ended")).toBeInTheDocument();
    });

    it("B-052 a real 401 after that check has answered still ends the session", async () => {
      await signInFirst();
      await act(async () => answerMe({ status: 401, body: { title: "Unauthorized" } }));
      await act(async () => FakeSocket.find("/ws/hops").drop(4401));
      await screen.findByRole("heading", { name: SIGN_IN });
    });
  });

  it("B-046 an admin before the first report gets changes and operations, and does not read as silent", async () => {
    await adminDashboard();
    await act(async () => monitor().open());
    await frames(...adminFramesBeforeReport([OPERATION], [CHANGE]));
    await screen.findByText("the only change");
    await pastStale();
    expect(screen.queryByText(UNKNOWN)).not.toBeInTheDocument();
    expect(screen.getByText("Deploy the app")).toBeInTheDocument();
  });
});
