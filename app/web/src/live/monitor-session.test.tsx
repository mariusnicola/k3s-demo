import { act, screen, waitFor } from "@testing-library/react";
import { vi } from "vitest";
import { adminMonitorFrames, visitorMonitorFrames } from "../mocks/fixtures";
import { FakeSocket, installApi, renderApp } from "../mocks/harness";
import { CHANGE, FRESH, OPERATION, SIGN_IN, UNKNOWN, adminDashboard, frames, fromServer, monitor, monitorSockets, pastStale, reopened, server, signIn, signOut, useCookieSockets } from "../mocks/monitorSession";

beforeEach(() => useCookieSockets());
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("T-015 the monitor socket follows the session", () => {
  it("T-015 signing in opens a new socket, so its handshake carries the new cookie", async () => {
    server(null);
    renderApp("/signin");
    await screen.findByRole("heading", { name: SIGN_IN });
    const first = monitor();
    await signIn();
    await waitFor(() => expect(first.closed).toBe(true));
    expect(monitor()).not.toBe(first);
  });

  it("T-015 signing out opens a new socket, so the old session's frames stop", async () => {
    await adminDashboard();
    const first = monitor();
    await signOut();
    expect(first.closed).toBe(true);
    await reopened();
    expect(monitor()).not.toBe(first);
  });

  it("T-015 a session that ends by itself opens a new socket", async () => {
    await adminDashboard();
    const first = monitor();
    await act(async () => FakeSocket.find("/ws/hops").drop(4401));
    await screen.findByRole("heading", { name: SIGN_IN });
    expect(first.closed).toBe(true);
    expect(monitor()).not.toBe(first);
  });

  it("T-015 a page load with a session keeps its one socket: the first handshake had the cookie", async () => {
    await adminDashboard();
    expect(monitorSockets()).toHaveLength(1);
  });

  it("T-015 a page load with no session keeps its one socket", async () => {
    server(null);
    renderApp("/signin");
    await screen.findByRole("heading", { name: SIGN_IN });
    expect(monitorSockets()).toHaveLength(1);
  });
});

describe("T-015 signing out leaves nothing of the session", () => {
  it("T-015 operations go", async () => {
    await adminDashboard();
    await act(async () => monitor().open());
    await frames(...adminMonitorFrames([OPERATION], [], FRESH));
    await screen.findByText("Deploy the app");
    await signOut();
    expect(screen.queryByText("Deploy the app")).not.toBeInTheDocument();
  });

  it("T-015 the report and the changes go, so the next user does not get them from memory", async () => {
    await adminDashboard();
    await act(async () => monitor().open());
    await frames(...adminMonitorFrames([], [CHANGE], FRESH));
    await screen.findByText("the only change");
    expect(screen.getByText("cosmos")).toBeInTheDocument();
    await signOut();
    await signIn();
    await screen.findByRole("heading", { name: "Architecture" });
    expect(screen.queryByText("the only change")).not.toBeInTheDocument();
    expect(screen.queryByText("cosmos")).not.toBeInTheDocument();
  });
});

describe("T-015 a socket with no report frames is not a silent one", () => {
  it("T-015 an admin who signed out leaves no old report age behind", async () => {
    await adminDashboard();
    await act(async () => monitor().open());
    await frames(...fromServer());
    await signOut();
    await reopened();
    await act(async () => monitor().open());
    await frames(...fromServer());
    await pastStale();
    expect(screen.queryByText(UNKNOWN)).not.toBeInTheDocument();
  });

  it("T-015 a visitor who only ever gets operations does not read as silent", async () => {
    server(null);
    renderApp("/signin");
    await screen.findByRole("heading", { name: SIGN_IN });
    await act(async () => monitor().open());
    await frames(...visitorMonitorFrames());
    await pastStale();
    expect(screen.queryByText(UNKNOWN)).not.toBeInTheDocument();
  });

  it("T-015 a signed-in non-admin who only ever gets operations does not read as silent, and running work stays live", async () => {
    server("viewer");
    renderApp("/dashboard");
    await screen.findByRole("heading", { name: "Dashboard" });
    await act(async () => monitor().open());
    await frames(...visitorMonitorFrames([OPERATION]));
    await pastStale();
    expect(screen.queryByText(UNKNOWN)).not.toBeInTheDocument();
    expect(screen.getByText("Deploy the app").closest(".ds-operation")!.querySelector(".ds-spinner")).not.toBeNull();
  });

  it("T-015 a non-admin whose socket is really lost still reads as unknown", async () => {
    server("viewer");
    renderApp("/dashboard");
    await screen.findByRole("heading", { name: "Dashboard" });
    await act(async () => monitor().open());
    await frames(...visitorMonitorFrames());
    await act(async () => monitor().drop());
    expect(await screen.findByText(UNKNOWN)).toBeInTheDocument();
  });

  it("T-015 a report frame still starts the age test, so an admin's silent socket is caught", async () => {
    await adminDashboard();
    await act(async () => monitor().open());
    await frames(...adminMonitorFrames([], [], FRESH));
    await pastStale();
    expect(await screen.findByText(UNKNOWN)).toBeInTheDocument();
  });
});

describe("T-015 an admin session that expires on the monitor socket", () => {
  const expire = () => act(async () => monitor().drop(4401));

  it("T-015 ends the session and comes back as one visitor connection, so the banner returns", async () => {
    await adminDashboard();
    await act(async () => monitor().open());
    await frames(...adminMonitorFrames([], [], FRESH));
    await expire();
    await screen.findByRole("heading", { name: SIGN_IN });
    expect(monitorSockets()).toHaveLength(2);
    await act(async () => monitor().open());
    await frames(...visitorMonitorFrames([OPERATION]));
    expect(await screen.findByText("Deploy the app")).toBeInTheDocument();
    expect(screen.queryByText(UNKNOWN)).not.toBeInTheDocument();
  });

  it("T-015 comes back even when the page holds no session of its own", async () => {
    server(null);
    installApi({ "GET /api/me": { status: 500, body: { title: "Failed" } } });
    renderApp("/batches");
    await screen.findByText("Your session could not be checked.");
    await act(async () => monitor().open());
    await frames(...adminMonitorFrames([], [], FRESH));
    await expire();
    await waitFor(() => expect(monitorSockets()).toHaveLength(2));
    await act(async () => monitor().open());
    await frames(...visitorMonitorFrames());
    await pastStale();
    expect(screen.queryByText(UNKNOWN)).not.toBeInTheDocument();
  });

  it("T-015 another socket's 4401 still only ends the session once", async () => {
    await adminDashboard();
    await act(async () => FakeSocket.find("/ws/hops").drop(4401));
    await screen.findByRole("heading", { name: SIGN_IN });
    expect(monitorSockets()).toHaveLength(2);
  });
});
