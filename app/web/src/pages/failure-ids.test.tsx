import { act, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { vi } from "vitest";
import { client, postBrowserReport } from "../api/client";
import { startBrowserReports } from "../api/reports";
import { ConnectionStatus, ErrorBoundary, ReportLogsLink } from "../ds";
import { lineSnapshot } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type ApiCall, type Reply } from "../mocks/harness";

const REPORTS = "POST /api/browser-reports";
const ACCEPTED = { status: 202, body: { id: "x" } };
const DOWN = { status: 503, body: { title: "Down", detail: "The store is away" } };
const UUID = /[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}/;

const posted = (calls: ApiCall[]) => calls.filter((call) => call.url.endsWith("/api/browser-reports")).map((call) => call.body as Record<string, unknown>);
const shownId = async () => (await screen.findAllByText(UUID))[0]!;

let stop: () => void;

function page(routes: Record<string, Reply>, user: "ada" | "olivia" = "ada") {
  const calls = installApi({ "GET /api/me": meRoute(user), "GET /api/batches": { body: [] }, [REPORTS]: ACCEPTED, ...routes });
  FakeSocket.greet("/line/ws", lineSnapshot());
  renderApp("/batches");
  return calls;
}

beforeEach(() => {
  useFakeSockets();
  stop = startBrowserReports(postBrowserReport);
});

afterEach(() => {
  stop();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("REQ-BT-056 a failed request on a page", () => {
  it("REQ-BT-056 shows the ID the report went out with, and links an admin to it", async () => {
    const calls = page({ "GET /api/batches": DOWN });
    const id = (await shownId()).textContent!;
    expect(posted(calls)).toEqual([expect.objectContaining({ id, kind: "request-failure", status: 503 })]);
    expect(screen.getByRole("link", { name: `Open report ${id} in Logs` })).toHaveAttribute("href", `/logs?reportId=${id}`);
    expect(screen.getByRole("button", { name: "Copy report ID" })).toBeInTheDocument();
  });

  it("REQ-BT-056 shows no link to someone Logs would turn away", async () => {
    page({ "GET /api/batches": DOWN }, "olivia");
    const id = (await shownId()).textContent!;
    expect(screen.queryByRole("link", { name: `Open report ${id} in Logs` })).not.toBeInTheDocument();
  });

  it("REQ-BT-056 still shows the ID when the report itself is lost", async () => {
    page({ "GET /api/batches": DOWN, [REPORTS]: { status: 500 } });
    expect(await shownId()).toBeInTheDocument();
  });

  it("REQ-BT-056 a session check that failed shows its ID too", async () => {
    installApi({ "GET /api/me": { status: 500 }, [REPORTS]: ACCEPTED });
    renderApp("/batches");
    expect(await shownId()).toBeInTheDocument();
  });
});

describe("REQ-BT-056 a lost socket", () => {
  async function pageWithSocket() {
    const calls = page({});
    await waitFor(() => FakeSocket.find("/ws/hops"));
    await act(async () => FakeSocket.find("/ws/hops").open());
    return calls;
  }

  it("REQ-BT-056 reports with no request behind it and shows the ID beside the status", async () => {
    const calls = await pageWithSocket();
    await act(async () => FakeSocket.find("/ws/hops").drop(1006));
    const id = (await shownId()).textContent!;
    expect(posted(calls)).toEqual([expect.objectContaining({ id, kind: "socket-failure", status: null, correlationId: null })]);
    expect(String(posted(calls)[0]!.message)).toContain("1006");
    expect(screen.getAllByRole("link", { name: `Open report ${id} in Logs` }).length).toBeGreaterThan(0);
  });

  it("REQ-BT-056 a 4401 is the session ending, not a failure of the socket", async () => {
    const calls = await pageWithSocket();
    await act(async () => FakeSocket.find("/ws/hops").drop(4401));
    await screen.findByRole("heading", { name: "Sign in to Batch Tracker" });
    expect(posted(calls)).toEqual([]);
  });

  it("REQ-BT-056 the ID goes once the socket is back", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    await pageWithSocket();
    const socket = FakeSocket.find("/ws/hops");
    await act(async () => socket.drop(1006));
    expect(screen.getAllByText(UUID).length).toBeGreaterThan(0);
    await act(async () => vi.advanceTimersByTimeAsync(1_000));
    await act(async () => FakeSocket.find("/ws/hops").open());
    expect(screen.queryByText(UUID)).not.toBeInTheDocument();
    vi.useRealTimers();
  });
});

describe("REQ-BT-056 stale data on an open socket", () => {
  it("REQ-BT-056 is no socket failure, so an ID left from an earlier loss does not show", () => {
    render(<ConnectionStatus label="Live updates" stale now={Date.now()} state={{ status: "open", lastDataAt: Date.now(), reportId: "6f1c2a34-1b2c-4d3e-8f90-a1b2c3d4e5f6" }} />);
    expect(screen.getByText("Live updates: data stale")).toBeInTheDocument();
    expect(screen.queryByText(UUID)).not.toBeInTheDocument();
  });
});

describe("REQ-BT-056 a value thrown in the page and shown there", () => {
  it("REQ-BT-056 a failed read shows its ID and reports once", async () => {
    vi.spyOn(client, "listBatches").mockRejectedValue(new TypeError("Page bug"));
    const calls = page({});
    const id = (await shownId()).textContent!;
    expect(screen.getByText("This page hit an error")).toBeInTheDocument();
    await act(async () => new Promise((resolve) => setTimeout(resolve)));
    expect(posted(calls)).toEqual([expect.objectContaining({ id, kind: "render-fault", status: null, correlationId: null, message: "Page bug" })]);
  });

  it("REQ-BT-056 a session check shows its ID", async () => {
    vi.spyOn(client, "restoreSession").mockRejectedValue(new TypeError("Session bug"));
    const calls = installApi({ [REPORTS]: ACCEPTED });
    renderApp("/batches");
    const id = (await shownId()).textContent!;
    expect(posted(calls)).toEqual([expect.objectContaining({ id, message: "Session bug" })]);
  });

});

describe("REQ-BT-056 a render fault", () => {
  function Broken(): never {
    throw new Error("The list could not draw");
  }

  it("REQ-BT-056 sends one report, through React's own console.error", async () => {
    stop();
    const logged = vi.spyOn(console, "error").mockImplementation(() => undefined);
    stop = startBrowserReports(postBrowserReport);
    const calls = installApi({ [REPORTS]: ACCEPTED });
    render(
      <MemoryRouter>
        <ErrorBoundary>
          <Broken />
        </ErrorBoundary>
      </MemoryRouter>,
    );
    await screen.findByRole("alert");
    await act(async () => new Promise((resolve) => setTimeout(resolve)));
    expect(logged).toHaveBeenCalled();
    expect(posted(calls)).toHaveLength(1);
    expect(posted(calls)[0]).toMatchObject({ kind: "render-fault", message: "The list could not draw" });
  });

  it("B-066 a thrown string sends one report too, through React's own console.error", async () => {
    function BrokenWords(): never {
      throw "Plain words";
    }
    stop();
    const logged = vi.spyOn(console, "error").mockImplementation(() => undefined);
    stop = startBrowserReports(postBrowserReport);
    const calls = installApi({ [REPORTS]: ACCEPTED });
    render(
      <MemoryRouter>
        <ErrorBoundary>
          <BrokenWords />
        </ErrorBoundary>
      </MemoryRouter>,
    );
    await screen.findByRole("alert");
    await act(async () => new Promise((resolve) => setTimeout(resolve)));
    expect(logged).toHaveBeenCalled();
    expect(posted(calls)).toEqual([expect.objectContaining({ kind: "render-fault", message: "Plain words" })]);
  });

  it("REQ-BT-056 reports and shows the ID on the crash page", async () => {
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    const calls = installApi({ [REPORTS]: ACCEPTED });
    render(
      <MemoryRouter>
        <ReportLogsLink.Provider value>
          <ErrorBoundary>
            <Broken />
          </ErrorBoundary>
        </ReportLogsLink.Provider>
      </MemoryRouter>,
    );
    const crash = await screen.findByRole("alert");
    const id = within(crash).getByText(UUID, { selector: "code" }).textContent!;
    expect(posted(calls)).toContainEqual(expect.objectContaining({ id, kind: "render-fault", status: null, correlationId: null, message: "The list could not draw" }));
    expect(within(crash).getByRole("link", { name: `Open report ${id} in Logs` })).toBeInTheDocument();
  });
});
