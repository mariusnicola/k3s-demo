import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { REQUEST_LIMIT_MS } from "../../config";
import { LINE_STOPPED, lineLogs, lineSnapshot, serviceEntry, serviceLogs } from "../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Handler, type Reply } from "../../mocks/harness";

const FAILED: Reply = { status: 500, body: { title: "Failed" } };
const NO_LINE_ROWS: Reply = { body: lineLogs({ matched: 0, byReason: {}, entries: [] }) };
const NO_SERVICE_ROWS: Reply = { body: serviceLogs({ matched: 0, entries: [] }) };
const LINE_ONLY_NOTE = /Showing production line entries only/;
const never = () => new Promise<Reply>(() => undefined);

function open(line: Reply | Handler, live: Reply | Handler, path = "/logs") {
  const calls = installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": line, "GET /live/logs": live });
  FakeSocket.greet("/line/ws", lineSnapshot({ status: { ...LINE_STOPPED, errors: 1, errorsByReason: { "event-timeout": 1 } } }));
  renderApp(path);
  return calls;
}

const liveReads = (calls: ReturnType<typeof installApi>) => calls.filter((c) => c.url.includes("/live/logs"));

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

describe("REQ-BT-052 the page claims only what it read", () => {
  it("REQ-BT-052 both reads failed: no claim that Line entries are shown", async () => {
    open(FAILED, FAILED);
    await screen.findByText("Reading the service logs");
    await screen.findByText("Reading the logs");
    expect(screen.queryByText(LINE_ONLY_NOTE)).not.toBeInTheDocument();
  });

  it("REQ-BT-052 the Line still pending: no claim that its entries are shown", async () => {
    open(never, FAILED);
    await screen.findByText("Reading the service logs");
    expect(screen.queryByText(LINE_ONLY_NOTE)).not.toBeInTheDocument();
  });

  it("REQ-BT-052 an empty Line with a failed service log does not say nothing was logged", async () => {
    open(NO_LINE_ROWS, FAILED);
    expect(await screen.findByText(/service log could not be read/)).toBeInTheDocument();
    expect(screen.queryByText("Nothing logged yet")).not.toBeInTheDocument();
    expect(screen.queryByText(LINE_ONLY_NOTE)).not.toBeInTheDocument();
  });

  it("REQ-BT-052 filters with a failed service log do not say nothing matches", async () => {
    open(NO_LINE_ROWS, FAILED, "/logs?level=error");
    expect(await screen.findByText(/service log could not be read/)).toBeInTheDocument();
    expect(screen.queryByText("No entries match these filters")).not.toBeInTheDocument();
  });

  it("REQ-BT-052 an empty Line waits for the service log before saying nothing was logged", async () => {
    let release = (_: Reply) => undefined as void;
    const calls = open(NO_LINE_ROWS, () => new Promise<Reply>((resolve) => (release = resolve)));
    await waitFor(() => expect(liveReads(calls)).toHaveLength(1));
    await screen.findByText("Logs", { selector: "h1, h2" });
    await waitFor(() => expect(screen.getByLabelText("Loading log entries")).toBeInTheDocument());
    expect(screen.queryByText("Nothing logged yet")).not.toBeInTheDocument();
    release(NO_SERVICE_ROWS);
    expect(await screen.findByText("Nothing logged yet")).toBeInTheDocument();
  });
});

describe("REQ-BT-052 what the latest read says, not an older one", () => {
  it("REQ-BT-052 an empty Line with a failed re-read does not say nothing was logged", async () => {
    const user = userEvent.setup();
    let answers = 0;
    open(NO_LINE_ROWS, () => (++answers === 1 ? NO_SERVICE_ROWS : FAILED));
    await screen.findByText("Nothing logged yet");
    await user.click(screen.getByRole("button", { name: "Read service entries again" }));
    expect(await screen.findByText(/service log could not be read/)).toBeInTheDocument();
    expect(screen.queryByText("Nothing logged yet")).not.toBeInTheDocument();
  });
});

describe("REQ-BT-052 a failed re-read of either log", () => {
  const dropFrame = () => FakeSocket.find("/line/ws").send({ ...lineSnapshot(), type: "line", at: "2026-10-31T11:00:00.000Z", entries: [], entriesDropped: 2 });
  const failSecond = (first: Reply) => {
    let answers = 0;
    return () => (++answers === 1 ? first : FAILED);
  };
  const reread = async () => {
    await screen.findByText("Nothing logged yet");
    await act(async () => dropFrame());
    await userEvent.click(await screen.findByRole("button", { name: "Read the newest entries again" }));
  };

  it("REQ-BT-052 an empty Line with a failed re-read does not say nothing was logged", async () => {
    open(failSecond(NO_LINE_ROWS), NO_SERVICE_ROWS);
    await reread();
    expect(await screen.findByText(/production line log could not be read/)).toBeInTheDocument();
    expect(screen.queryByText("Nothing logged yet")).not.toBeInTheDocument();
  });

  it("REQ-BT-052 filters do not say nothing matches when the Line re-read failed", async () => {
    open(failSecond(NO_LINE_ROWS), NO_SERVICE_ROWS, "/logs?farm=farm-a");
    await screen.findByText("No entries match these filters");
    await act(async () => dropFrame());
    await userEvent.click(await screen.findByRole("button", { name: "Read the newest entries again" }));
    expect(await screen.findByText(/production line log could not be read/)).toBeInTheDocument();
    expect(screen.queryByText("No entries match these filters")).not.toBeInTheDocument();
  });

  it("REQ-BT-052 both re-reads failed: both logs are named", async () => {
    open(failSecond(NO_LINE_ROWS), failSecond(NO_SERVICE_ROWS));
    await reread();
    expect(await screen.findByText(/production line and service logs could not be read/)).toBeInTheDocument();
    expect(screen.queryByText("Nothing logged yet")).not.toBeInTheDocument();
  });
});

describe("REQ-BT-045 an empty list after a dropped frame", () => {
  const dropFrame = () => FakeSocket.find("/line/ws").send({ ...lineSnapshot(), type: "line", at: "2026-10-31T11:00:00.000Z", entries: [], entriesDropped: 2 });

  it.each([
    ["with no filter", "/logs", "Nothing logged yet"],
    ["with a filter", "/logs?farm=farm-a", "No entries match these filters"],
  ])("REQ-BT-045 %s does not contradict the note that entries were left out", async (_name, path, emptyWords) => {
    open(NO_LINE_ROWS, NO_SERVICE_ROWS, path);
    await screen.findByText(emptyWords);
    await act(async () => dropFrame());
    expect(await screen.findByText(/Entries came too fast/)).toBeInTheDocument();
    expect(screen.queryByText(emptyWords)).not.toBeInTheDocument();
    expect(screen.getByText(/came too fast to list\. So this list may be missing entries/)).toBeInTheDocument();
  });
});

describe("REQ-BT-052 what the page read when it reads one log", () => {
  const ONLY_LINE = /Only the production line log was read/;

  it.each(["/logs?level=info", "/logs?reason=event-timeout"])("REQ-BT-052 %s says only the Line was read", async (path) => {
    const calls = open({ body: lineLogs() }, { body: serviceLogs() }, path);
    expect(await screen.findByText(ONLY_LINE)).toBeInTheDocument();
    expect(liveReads(calls)).toHaveLength(0);
  });

  it.each(["/logs", "/logs?level=error", "/logs?source=line"])("REQ-BT-052 %s needs no such note", async (path) => {
    open({ body: lineLogs() }, { body: serviceLogs() }, path);
    await screen.findByRole("table", { name: "Log entries" });
    expect(screen.queryByText(ONLY_LINE)).not.toBeInTheDocument();
  });
});

describe("REQ-BT-050 a service read nothing answers", () => {
  it("REQ-BT-050 ends the wait: an empty Line stops loading and says the service log could not be read", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      open(NO_LINE_ROWS, (_url, init) => new Promise<Reply>((_ok, reject) => init.signal!.addEventListener("abort", () => reject(init.signal!.reason))));
      await screen.findByLabelText("Loading log entries");
      await act(async () => {
        await vi.advanceTimersByTimeAsync(REQUEST_LIMIT_MS);
      });
      expect(await screen.findByText(/service log could not be read/)).toBeInTheDocument();
      expect(screen.queryByLabelText("Loading log entries")).not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("REQ-BT-045 the Dashboard's Errors link", () => {
  const TWO = serviceLogs({ matched: 1, entries: [serviceEntry()] });

  it("REQ-BT-045 lands on what the counter counted: Line errors, with no service rows", async () => {
    const calls = open({ body: lineLogs() }, { body: TWO }, "/logs?level=error&source=line");
    expect(await screen.findByText("Matches the Dashboard: 1 error.")).toBeInTheDocument();
    const table = await screen.findByRole("table", { name: "Log entries" });
    expect(within(table).getAllByRole("row")).toHaveLength(2);
    expect(liveReads(calls)).toHaveLength(0);
    expect(screen.getByRole("combobox", { name: "Source" })).toHaveValue("line");
  });

  it("REQ-BT-045 widening Source to all reads the service log too", async () => {
    const calls = open({ body: lineLogs() }, { body: TWO }, "/logs?level=error&source=line");
    await screen.findByRole("table", { name: "Log entries" });
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "Source" }), "All sources");
    expect(await screen.findByText("A worker could not finish.")).toBeInTheDocument();
    expect(liveReads(calls)[0]!.url).not.toContain("source=");
  });

  it("REQ-BT-045 Source alone counts as a filter that can be cleared", async () => {
    open({ body: lineLogs() }, { body: TWO }, "/logs?source=line");
    expect(await screen.findByRole("button", { name: "Clear filters" })).toBeInTheDocument();
  });

  it("REQ-BT-045 an address with any other source reads every source", async () => {
    open({ body: lineLogs() }, { body: TWO }, "/logs?source=nope");
    expect(await screen.findByText("A worker could not finish.")).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Source" })).toHaveValue("");
    expect(screen.queryByRole("button", { name: "Clear filters" })).not.toBeInTheDocument();
  });

  it("REQ-BT-045 with every source shown the page says the Dashboard does not count service rows", async () => {
    open({ body: lineLogs() }, { body: TWO }, "/logs?level=error");
    await screen.findByText("A worker could not finish.");
    expect(screen.getByText(/the Dashboard does not count them/)).toBeInTheDocument();
  });
});

describe("REQ-BT-052 a farm or batch filter on service entries", () => {
  const NOTE = /No service entry names this farm or batch/;

  it.each(["/logs?farm=farm-a", "/logs?batch=b-1"])("REQ-BT-052 %s with no service rows says why they may be missing", async (path) => {
    open({ body: lineLogs() }, NO_SERVICE_ROWS, path);
    expect(await screen.findByText(NOTE)).toBeInTheDocument();
  });

  it("REQ-BT-052 says nothing once a service entry does name it", async () => {
    open({ body: lineLogs() }, { body: serviceLogs({ entries: [serviceEntry({ farm: "farm-a", batchId: "b-1", batchNumber: 7 })] }) }, "/logs?farm=farm-a");
    await screen.findByText("A worker could not finish.");
    expect(screen.queryByText(NOTE)).not.toBeInTheDocument();
  });

  it("REQ-BT-052 says nothing with no farm or batch filter", async () => {
    open({ body: lineLogs() }, NO_SERVICE_ROWS, "/logs?level=error");
    await screen.findByRole("table", { name: "Log entries" });
    expect(screen.queryByText(NOTE)).not.toBeInTheDocument();
  });
});
