import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { logKey } from "../../logic/feed/live";
import { LINE_STOPPED, lineLogs, lineSnapshot, serviceEntry, serviceLogs } from "../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Handler, type Reply } from "../../mocks/harness";

const LIVE = "GET /live/logs";

function page(live: Reply | Handler, path = "/logs", user: "ada" | "olivia" = "ada") {
  const calls = installApi({ "GET /api/me": meRoute(user), "GET /line/logs": { body: lineLogs() }, [LIVE]: live });
  FakeSocket.greet("/line/ws", lineSnapshot({ status: { ...LINE_STOPPED, errors: 1, errorsByReason: { "event-timeout": 1 } } }));
  renderApp(path);
  return calls;
}

const liveReads = (calls: ReturnType<typeof installApi>) => calls.filter((c) => c.url.includes("/live/logs"));
const table = () => screen.findByRole("table", { name: "Log entries" });
const rowTexts = async () => within(await table()).getAllByRole("row").slice(1).map((row) => row.textContent ?? "");

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

describe("REQ-BT-052 service log lines on the Logs page", () => {
  it("REQ-BT-052 lists Line and service entries together, newest first, naming where each came from", async () => {
    page({
      body: serviceLogs({
        entries: [
          serviceEntry({ at: "2026-10-31T10:30:00.000Z", message: "Newest from the worker" }),
          serviceEntry({ at: "2026-10-31T09:30:00.000Z", service: "api", message: "Oldest from the API" }),
        ],
      }),
    });
    await screen.findByText("Newest from the worker");
    const rows = await rowTexts();
    expect(rows).toHaveLength(3);
    expect(rows[0]).toMatch(/worker.*Newest from the worker/);
    expect(rows[1]).toMatch(/Line.*The Line stopped waiting/);
    expect(rows[2]).toMatch(/api.*Oldest from the API/);
  });

  it("REQ-BT-052 the correlation filter and the other filters reach the service log", async () => {
    const calls = page({ body: serviceLogs() }, "/logs?level=error&correlationId=corr-9&farm=farm-a");
    await screen.findByText("A worker could not finish.");
    const query = new URL(liveReads(calls)[0]!.url, "http://x").searchParams;
    expect(query.get("correlationId")).toBe("corr-9");
    expect(query.get("level")).toBe("error");
    expect(query.get("farm")).toBe("farm-a");
    expect(query.get("limit")).toBe("200");
  });

  it.each([
    ["Information", "/logs?level=info"],
    ["a Line reason", "/logs?reason=event-timeout"],
  ])("REQ-BT-052 %s has no service entries, and asking would be refused, so nothing is read", async (_name, path) => {
    const calls = page({ body: serviceLogs() }, path);
    await table();
    expect(liveReads(calls)).toHaveLength(0);
  });

  it("REQ-BT-052 a role without the permission never reads the service log", async () => {
    const calls = page({ status: 403 }, "/logs", "olivia");
    await screen.findByRole("heading", { name: "Dashboard" });
    expect(liveReads(calls)).toHaveLength(0);
  });

  it("REQ-BT-052 a service entry shows what the server said, with its service and pod, and no batch it never had", async () => {
    const user = userEvent.setup();
    page({ body: serviceLogs({ entries: [serviceEntry({ impact: "Words from the service", nextAction: "Next from the service" })] }) });
    await screen.findByText("A worker could not finish.");
    expect((await rowTexts()).join(" ")).not.toContain("Batch 0");
    await user.click(screen.getByRole("button", { name: /details of service-error/ }));
    expect(screen.getByText("Words from the service")).toBeInTheDocument();
    expect(screen.getByText("Next from the service")).toBeInTheDocument();
    expect(screen.getByText("worker-7c9")).toBeInTheDocument();
    expect(screen.getByText("Service worker, pod worker-7c9")).toBeInTheDocument();
  });

  it("REQ-BT-052 once the provider sends batch fields, a service entry on a batch links to it", async () => {
    page({ body: serviceLogs({ entries: [serviceEntry({ batchId: "b-9", farm: "farm-b", batchNumber: 9 })] }) });
    expect(await screen.findByRole("link", { name: "Batch 9" })).toHaveAttribute("href", "/batches/farm-b/b-9");
  });

  it("REQ-BT-052 two pods logging in the same millisecond stay two rows", () => {
    expect(logKey(serviceEntry({ pod: "worker-1" }))).not.toBe(logKey(serviceEntry({ pod: "worker-2" })));
    expect(logKey(serviceEntry({ message: "one" }))).not.toBe(logKey(serviceEntry({ message: "two" })));
  });

  it("REQ-BT-052 service entries are not pushed, so the page says so and reads again at a press", async () => {
    const user = userEvent.setup();
    const calls = page({ body: serviceLogs() });
    await screen.findByText("A worker could not finish.");
    expect(screen.getByText(/Service entries do not update live/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Read service entries again" }));
    await waitFor(() => expect(liveReads(calls)).toHaveLength(2));
  });

  it("REQ-BT-052 the press after lost frames reads the service log too, so its rows are as new as the Line's", async () => {
    const user = userEvent.setup();
    const calls = page({ body: serviceLogs() });
    await screen.findByText("A worker could not finish.");
    await act(async () => FakeSocket.find("/line/ws").send({ ...lineSnapshot(), type: "line", at: "2026-10-31T11:00:00.000Z", entries: [], entriesDropped: 2 }));
    await user.click(await screen.findByRole("button", { name: "Read the newest entries again" }));
    await waitFor(() => expect(liveReads(calls)).toHaveLength(2));
  });

  it("REQ-BT-052 a warning reads as a warning, not as plain information", async () => {
    page({ body: serviceLogs({ entries: [serviceEntry({ level: "warning", reason: "service-warning" })] }) });
    await screen.findByText("A worker could not finish.");
    expect(within(await table()).getByText("warning").closest(".ds-badge")).toHaveAttribute("data-tone", "warning");
  });

  it("REQ-BT-052 says when the service log held more than it sent, or lost older entries", async () => {
    page({ body: serviceLogs({ matched: 300, droppedByLimit: 299, evicted: 40 }) });
    expect(await screen.findByText("Showing 1 of 300 matching service entries and browser reports, the newest of each kind.")).toBeInTheDocument();
    expect(screen.getByText(/40 older service entries left memory/)).toBeInTheDocument();
  });
});

describe("REQ-BT-052 a failed Line read leaves the service rows on screen", () => {
  const openWith = (line: Reply | Handler, live: Reply | Handler) => {
    const calls = installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": line, [LIVE]: live });
    FakeSocket.greet("/line/ws", lineSnapshot());
    renderApp("/logs");
    return calls;
  };

  it("REQ-BT-052 shows them, says the Line read failed, and gives the server's ID", async () => {
    openWith({ status: 500, body: { title: "Failed", correlationId: "corr-line" } }, { body: serviceLogs() });
    expect(await screen.findByText("A worker could not finish.")).toBeInTheDocument();
    expect(await screen.findByText(/production line log could not be read/)).toBeInTheDocument();
    expect(screen.getByText("corr-line")).toBeInTheDocument();
  });

  it("REQ-BT-052 with no answer from the Line it gives the ID that was sent", async () => {
    const calls = openWith(() => Promise.reject(new TypeError("failed to fetch")), { body: serviceLogs() });
    expect(await screen.findByText("A worker could not finish.")).toBeInTheDocument();
    const sent = calls.find((c) => c.url.includes("/line/logs"))!.headers["X-Correlation-Id"]!;
    expect(await screen.findByText(sent)).toBeInTheDocument();
    expect(screen.getByText(/production line log could not be read/)).toBeInTheDocument();
  });

  it("REQ-BT-052 a press that brings the Line back shows both and drops the notice", async () => {
    const user = userEvent.setup();
    let answers = 0;
    openWith(() => (++answers === 1 ? { status: 500, body: { title: "Failed" } } : { body: lineLogs() }), { body: serviceLogs() });
    await screen.findByText(/production line log could not be read/);
    await user.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByText(/The Line stopped waiting/)).toBeInTheDocument();
    expect(screen.getByText("A worker could not finish.")).toBeInTheDocument();
    expect(screen.queryByText(/production line log could not be read/)).not.toBeInTheDocument();
  });

  it("REQ-BT-052 a failed re-read with Line rows on screen does not claim only service entries are shown", async () => {
    const user = userEvent.setup();
    let answers = 0;
    openWith(() => (++answers === 1 ? { body: lineLogs() } : { status: 500, body: { title: "Failed", correlationId: "corr-again" } }), { body: serviceLogs() });
    await screen.findByText("A worker could not finish.");
    await screen.findByText(/The Line stopped waiting/);
    await act(async () => FakeSocket.find("/line/ws").send({ ...lineSnapshot(), type: "line", at: "2026-10-31T11:00:00.000Z", entries: [], entriesDropped: 2 }));
    await user.click(await screen.findByRole("button", { name: "Read the newest entries again" }));
    expect(await screen.findByText("corr-again")).toBeInTheDocument();
    expect(screen.getByText(/The Line stopped waiting/)).toBeInTheDocument();
    expect(screen.queryByText(/production line log could not be read/)).not.toBeInTheDocument();
  });

  it("REQ-BT-052 with no service rows to show, the Line's failure keeps the whole panel", async () => {
    openWith({ status: 500, body: { title: "Failed" } }, { body: serviceLogs({ matched: 0, entries: [] }) });
    expect(await screen.findByText("Reading the logs")).toBeInTheDocument();
    expect(screen.queryByRole("table", { name: "Log entries" })).not.toBeInTheDocument();
    expect(screen.queryByText(/production line log could not be read/)).not.toBeInTheDocument();
  });
});

describe("REQ-BT-052 a refused service log leaves the Logs page working", () => {
  it("REQ-BT-052 a 403 keeps the Line entries and says only they are shown", async () => {
    page({ status: 403, body: { title: "Forbidden", correlationId: "corr-live" } });
    expect(await screen.findByText(/The Line stopped waiting/)).toBeInTheDocument();
    expect(await screen.findByText("Reading the service logs")).toBeInTheDocument();
    expect(screen.getByText("corr-live")).toBeInTheDocument();
    expect(screen.getByText(/Showing production line entries only/)).toBeInTheDocument();
    expect(screen.queryByText(/Service entries do not update live/)).not.toBeInTheDocument();
  });

  it("REQ-BT-052 a failed read recovers at a press, and the Line entries stay meanwhile", async () => {
    const user = userEvent.setup();
    let answers = 0;
    page(async () => (++answers === 1 ? { status: 500, body: { title: "Failed" } } : { body: serviceLogs() }));
    await screen.findByText("Reading the service logs");
    expect(screen.getByText(/The Line stopped waiting/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByText("A worker could not finish.")).toBeInTheDocument();
    expect(screen.queryByText("Reading the service logs")).not.toBeInTheDocument();
  });

  it("REQ-BT-052 a failed second read says the service rows on screen are the last good ones", async () => {
    const user = userEvent.setup();
    let answers = 0;
    page(async () => (++answers === 1 ? { body: serviceLogs() } : { status: 500, body: { title: "Failed" } }));
    await screen.findByText("A worker could not finish.");
    await user.click(screen.getByRole("button", { name: "Read service entries again" }));
    expect(await screen.findByText(/from the last good read/)).toBeInTheDocument();
    expect(screen.getByText("A worker could not finish.")).toBeInTheDocument();
    expect(screen.queryByText(/Showing production line entries only/)).not.toBeInTheDocument();
  });

  it("REQ-BT-052 no answer from the service log changes nothing on the Line side", async () => {
    page(() => Promise.reject(new TypeError("failed to fetch")));
    expect(await screen.findByText(/Showing production line entries only/)).toBeInTheDocument();
    expect(screen.getByText(/The Line stopped waiting/)).toBeInTheDocument();
  });
});
