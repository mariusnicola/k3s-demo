import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import type { LineLogs } from "../../api/types";
import { LINE_STOPPED, lineLogs, lineSnapshot, logEntry } from "../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Handler, type Reply } from "../../mocks/harness";

const FROM_COUNTER = "/logs?level=error&source=line";

function logs(path: string, reply: Reply | Handler, errors = 1, byReason: Record<string, number> = { "event-timeout": errors }) {
  const calls = installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": reply });
  const evicted = typeof reply === "function" ? 0 : ((reply.body as LineLogs | undefined)?.evicted ?? 0);
  FakeSocket.greet("/line/ws", lineSnapshot({ evicted, status: { ...LINE_STOPPED, errors, errorsByReason: byReason } }));
  renderApp(path);
  return calls;
}

const reads = (calls: ReturnType<typeof installApi>) => calls.filter((c) => c.url.includes("/line/logs"));

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

describe("arriving from the dashboard", () => {
  it("REQ-BT-045 the Errors counter is a link a keyboard can reach, to logs filtered to errors", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": { body: lineLogs() } });
    FakeSocket.greet("/line/ws", lineSnapshot({ status: { ...LINE_STOPPED, errors: 3 } }));
    renderApp("/dashboard");
    const link = await screen.findByRole("link", { name: "3 walks dropped, open the logs" });
    expect(link).toHaveAttribute("href", "/logs?level=error&source=line");
    await userEvent.click(link);
    expect(await screen.findByRole("heading", { name: "Logs" })).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Level" })).toHaveValue("error");
  });

  it("REQ-BT-045 the level already set is sent to the service", async () => {
    const calls = logs(FROM_COUNTER, { body: lineLogs() });
    await screen.findByText("Count by reason");
    expect(reads(calls)[0]!.url).toContain("level=error");
    expect(reads(calls)[0]!.url).toContain("limit=200");
    expect(reads(calls)[0]!.url).not.toContain("source=");
  });

  it("REQ-BT-045 a non admin lands on their dashboard, and no read happens", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia") });
    renderApp("/logs");
    expect(await screen.findByRole("heading", { name: "Batches by step" })).toBeInTheDocument();
    expect(reads(calls)).toEqual([]);
  });

  it("REQ-BT-045 Logs is in the navigation", async () => {
    logs(FROM_COUNTER, { body: lineLogs() });
    expect(await screen.findByRole("link", { name: "Logs" })).toHaveAttribute("href", "/logs");
  });
});

describe("reasons and filters", () => {
  const two = lineLogs({ matched: 3, byReason: { "event-timeout": 2, "http-refused": 1 } });
  const sinceStart = { "event-timeout": 2, "http-refused": 1 };

  it("REQ-BT-045 From and To say they apply on leaving the field", async () => {
    logs("/logs", { body: lineLogs() });
    expect(await screen.findByRole("textbox", { name: "Batch" })).toBeInTheDocument();
    expect(screen.getByLabelText("From")).toHaveAccessibleDescription("Applies when you leave the field or press Enter.");
    expect(screen.getByLabelText("To")).toHaveAccessibleDescription("Applies when you leave the field or press Enter.");
  });

  it("REQ-BT-045 each reason shows its count and plain sentence, and pressing it filters", async () => {
    const calls = logs(FROM_COUNTER, { body: two }, 3, sinceStart);
    const reason = await screen.findByRole("button", { name: /The API refused a request/ });
    expect(reason).toHaveTextContent("1");
    expect(reason).toHaveAttribute("aria-pressed", "false");
    await userEvent.click(reason);
    await waitFor(() => expect(reads(calls).at(-1)!.url).toContain("reason=http-refused"));
    expect(await screen.findByRole("button", { name: /The API refused a request/ })).toHaveAttribute("aria-pressed", "true");
  });

  it("REQ-BT-045 pressing the selected reason clears it", async () => {
    const calls = logs(`${FROM_COUNTER}&reason=event-timeout`, { body: two }, 3, sinceStart);
    await userEvent.click(await screen.findByRole("button", { name: /The Line gave up waiting/ }));
    await waitFor(() => expect(reads(calls).at(-1)!.url).not.toContain("reason="));
  });

  it.each([
    ["reason", "reason=event-timeout"],
    ["farm", "farm=farm-b"],
    ["batch", "batch=42"],
    ["from", "from=2026-10-31T09%3A00%3A00.000Z"],
    ["to", "to=2026-10-31T10%3A00%3A00.000Z"],
  ])("REQ-BT-045 %s in the address reaches the service", async (_name, param) => {
    const calls = logs(`/logs?${param}`, { body: lineLogs() });
    await screen.findByText("Count by reason");
    expect(decodeURIComponent(reads(calls)[0]!.url)).toContain(decodeURIComponent(param));
  });

  it("REQ-BT-045 a nonsense address is dropped, not sent", async () => {
    const calls = logs("/logs?level=nope&reason=nope&farm=farm-z&from=yesterday", { body: lineLogs() });
    await screen.findByText("Count by reason");
    expect(reads(calls)[0]!.url).not.toMatch(/level=|reason=|farm=|from=/);
  });

  it("REQ-BT-045 the batch filter applies on Enter and goes in the address", async () => {
    const calls = logs(FROM_COUNTER, { body: lineLogs() });
    await userEvent.type(await screen.findByRole("textbox", { name: /Batch/ }), "42{Enter}");
    await waitFor(() => expect(reads(calls).at(-1)!.url).toContain("batch=42"));
  });

  it("REQ-BT-045 typing does not read once per character", async () => {
    const calls = logs(FROM_COUNTER, { body: lineLogs() });
    const field = await screen.findByRole("textbox", { name: /Batch/ });
    const before = reads(calls).length;
    await userEvent.type(field, "123");
    expect(reads(calls)).toHaveLength(before);
  });

  it("REQ-BT-045 Clear filters shows everything again", async () => {
    const calls = logs(`${FROM_COUNTER}&farm=farm-a`, { body: lineLogs() });
    await userEvent.click(await screen.findByRole("button", { name: "Clear filters" }));
    await waitFor(() => expect(reads(calls).at(-1)!.url).not.toMatch(/level=|farm=/));
    expect(await screen.findByRole("combobox", { name: "Level" })).toHaveValue("");
  });
});

describe("entries", () => {
  it("REQ-BT-045 a row shows time, reason, a batch link and the step", async () => {
    logs(FROM_COUNTER, { body: lineLogs() });
    const table = await screen.findByRole("table", { name: "Log entries" });
    expect(within(table).getByRole("cell", { name: /stopped waiting after 30 s/ })).toBeInTheDocument();
    expect(within(table).getByRole("link", { name: "Batch 7" })).toHaveAttribute("href", "/batches/farm-a/b-1");
    expect(within(table).getByText("Dose 1")).toBeInTheDocument();
  });

  it("REQ-BT-045 expanding shows every detail field", async () => {
    logs(FROM_COUNTER, { body: lineLogs() });
    await userEvent.click(await screen.findByRole("button", { name: /Show details of event-timeout/ }));
    for (const label of ["Time", "Level", "Reason code", "What happened", "Batch number", "Batch ID", "Farm", "Next step wanted", "Waited", "Last HTTP status", "Exception type", "Exception message", "Trace ID"]) {
      expect(screen.getAllByText(label).length).toBeGreaterThan(0);
    }
    expect(screen.getByText("TaskCanceledException")).toBeInTheDocument();
    expect(screen.getByText("The request was canceled.")).toBeInTheDocument();
    expect(screen.getByText("3,000 ms")).toBeInTheDocument();
    expect(screen.getByText("503")).toBeInTheDocument();
  });

  it("REQ-BT-045 fields the entry lacks say so", async () => {
    logs(FROM_COUNTER, { body: lineLogs({ entries: [logEntry({ exceptionType: null, exceptionMessage: null, httpStatus: null, batchId: null, traceId: null })] }) });
    await userEvent.click(await screen.findByRole("button", { name: /Show details/ }));
    expect(screen.getAllByText("None recorded").length).toBe(5);
    expect(screen.getByText("Batch 7, no ID recorded")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /Batch 7/ })).toBeNull();
  });

  it("REQ-BT-045 the expand control is keyboard reachable and says its state", async () => {
    logs(FROM_COUNTER, { body: lineLogs() });
    const button = await screen.findByRole("button", { name: /Show details/ });
    button.focus();
    await userEvent.keyboard("{Enter}");
    expect(button).toHaveAttribute("aria-expanded", "true");
    expect(document.getElementById(button.getAttribute("aria-controls")!)).toHaveTextContent("Exception type");
  });

  it("REQ-BT-045 an open row stays open when new data arrives", async () => {
    logs(FROM_COUNTER, { body: lineLogs() });
    fireEvent.click(await screen.findByRole("button", { name: /Show details/ }));
    act(() => FakeSocket.find("/line/ws").send({ ...lineSnapshot(), type: "line", entries: [logEntry({ at: "2026-10-31T11:00:00.000Z", exceptionType: "NewOne" })] }));
    expect(await screen.findByRole("button", { name: /Hide details/ })).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: /Show details/ })).toHaveLength(1);
    expect(screen.getByText("TaskCanceledException")).toBeInTheDocument();
  });
});

describe("states", () => {
  it("REQ-BT-045 loading", async () => {
    logs(FROM_COUNTER, () => new Promise<Reply>(() => undefined));
    expect(await screen.findByRole("status", { name: "Loading log entries" })).toBeInTheDocument();
  });

  it("REQ-BT-045 empty with no filters", async () => {
    logs("/logs", { body: lineLogs({ entries: [], matched: 0, byReason: {} }) }, 0);
    expect(await screen.findByText("Nothing logged yet")).toBeInTheDocument();
  });

  it("REQ-BT-045 empty with filters offers Clear filters", async () => {
    logs(FROM_COUNTER, { body: lineLogs({ entries: [], matched: 0, byReason: {} }) }, 0);
    expect(await screen.findByText("No entries match these filters")).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Clear filters" }).length).toBeGreaterThan(0);
  });

  it("REQ-BT-045 the service failing at load shows why and Try again", async () => {
    logs(FROM_COUNTER, { status: 503 });
    expect(await screen.findByRole("alert")).toHaveTextContent("The request failed");
    expect(screen.getByRole("button", { name: "Try again" })).toBeInTheDocument();
  });

  it("REQ-BT-045 a failed read of skipped entries keeps the entries and says so", async () => {
    let fail = false;
    logs(FROM_COUNTER, () => (fail ? { status: 503 } : { body: lineLogs() }));
    await screen.findByRole("table", { name: "Log entries" });
    fail = true;
    act(() => FakeSocket.find("/line/ws").send({ ...lineSnapshot(), type: "line", entriesDropped: 3 }));
    await userEvent.click(await screen.findByRole("button", { name: "Read the newest entries again" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The request failed");
    expect(screen.getByRole("table", { name: "Log entries" })).toBeInTheDocument();
  });

  it("REQ-BT-045 a lost connection says what is missing, with the last read time", async () => {
    logs(FROM_COUNTER, { body: lineLogs() });
    await screen.findByRole("table", { name: "Log entries" });
    act(() => FakeSocket.find("/line/ws").drop());
    expect(await screen.findByText("Live updates are lost")).toBeInTheDocument();
    expect(screen.getByText(/are missing until the stream returns/)).toBeInTheDocument();
  });

  it("REQ-BT-045 dropped entries are announced with their count", async () => {
    logs(FROM_COUNTER, { body: lineLogs({ evicted: 500 }) }, 501);
    expect(await screen.findByText("500 older entries left memory")).toBeInTheDocument();
  });

  it("REQ-BT-045 a cut off list says how much it shows", async () => {
    logs(FROM_COUNTER, { body: lineLogs({ matched: 900, droppedByLimit: 899 }) }, 900);
    expect(await screen.findByText(/Showing the newest 1 of 900 matching entries/)).toBeInTheDocument();
  });

  it("REQ-BT-045 nothing reads on a timer", async () => {
    const calls = logs(FROM_COUNTER, { body: lineLogs() });
    await screen.findByRole("table", { name: "Log entries" });
    const before = reads(calls).length;
    await act(() => new Promise<void>((resolve) => setTimeout(resolve, 2000)));
    expect(reads(calls)).toHaveLength(before);
  });
});

describe("counter and log agree or say why", () => {
  it("REQ-BT-045 equal numbers say they match", async () => {
    logs(FROM_COUNTER, { body: lineLogs({ matched: 4 }) }, 4);
    expect(await screen.findByText("Matches the Dashboard: 4 errors.")).toBeInTheDocument();
  });

  it("REQ-BT-045 errors that left memory explain the gap", async () => {
    logs(FROM_COUNTER, { body: lineLogs({ matched: 4, evicted: 3000 }) }, 10);
    expect(await screen.findByText(/counts 10 errors since the line started. 6 of them left memory, so 4 can be listed/)).toBeInTheDocument();
  });

  it("REQ-BT-045 a gap larger than what left memory is not blamed on memory", async () => {
    logs(FROM_COUNTER, { body: lineLogs({ matched: 4, evicted: 2 }) }, 10);
    expect(await screen.findByText(/says 10 errors and this log holds 4/)).toBeInTheDocument();
  });

  it("REQ-BT-045 counts come from the line's totals, which outlive memory", async () => {
    logs(FROM_COUNTER, { body: lineLogs({ matched: 1, byReason: { "event-timeout": 1 }, evicted: 9000 }) }, 40);
    expect(await screen.findByRole("button", { name: /The Line gave up waiting/ })).toHaveTextContent("40");
    expect(screen.getByText(/Since the line started/)).toBeInTheDocument();
  });

  it("REQ-BT-045 narrowed counts come from memory and say so", async () => {
    logs(`${FROM_COUNTER}&farm=farm-a`, { body: lineLogs({ byReason: { "event-timeout": 1 } }) }, 40);
    expect(await screen.findByRole("button", { name: /The Line gave up waiting/ })).toHaveTextContent("1");
    expect(screen.getByText(/Entries in memory that match these filters/)).toBeInTheDocument();
  });

  it("REQ-BT-045 a backwards range is said beside the field and never sent", async () => {
    const calls = logs("/logs?from=2026-10-31T10:00:00Z&to=2026-10-31T09:00:00Z", { body: lineLogs() });
    expect(await screen.findByText("To is before From, so it is not applied.")).toBeInTheDocument();
    expect(reads(calls)[0]!.url).not.toContain("to=");
  });

  it("REQ-BT-045 filters explain the gap", async () => {
    logs(`${FROM_COUNTER}&farm=farm-a`, { body: lineLogs({ matched: 1 }) }, 10);
    expect(await screen.findByText(/Showing 1 of the 10 errors on the Dashboard. The filters hide the rest/)).toBeInTheDocument();
  });

  it("REQ-BT-045 a difference with no cause is named", async () => {
    logs(FROM_COUNTER, { body: lineLogs({ matched: 4 }) }, 7);
    expect(await screen.findByText("The Dashboard says 7 errors and this log holds 4.")).toBeInTheDocument();
  });

  it("REQ-BT-045 an unreadable counter still shows the log and says so", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": { body: lineLogs() } });
    renderApp(FROM_COUNTER);
    expect(await screen.findByText(/counter could not be read/)).toBeInTheDocument();
    expect(screen.getByRole("table", { name: "Log entries" })).toBeInTheDocument();
  });
});
