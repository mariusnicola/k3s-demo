import { act, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { LINE_STOPPED, lineLogs, lineSnapshot, serviceEntry, serviceLogs } from "../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Handler, type Reply } from "../../mocks/harness";
import entriesNotices from "./EntriesNotices.tsx?raw";
import serviceNotices from "./ServiceNotices.tsx?raw";

const FAILED: Reply = { status: 500, body: { title: "Failed" } };
const LINE: Reply = { body: lineLogs({ matched: 0, byReason: {}, entries: [] }) };
const SERVICE: Reply = { body: serviceLogs({ matched: 0, entries: [] }) };
const lineWith = (over: Parameters<typeof lineLogs>[0]): Reply => ({ body: lineLogs({ matched: 0, byReason: {}, entries: [], ...over }) });
const serviceWith = (over: Parameters<typeof serviceLogs>[0]): Reply => ({ body: serviceLogs({ matched: 0, entries: [], ...over }) });
/** A flag, not a count of reads: how many reads a page makes on arrival is not this file's business. */
let readsFail = false;
const failsOnRequest = (first: Reply): Handler => () => (readsFail ? FAILED : first);
const failingFromNow = () => (readsFail = true);

const NOTHING_LOGGED = "Nothing logged yet";
const NOTHING_MATCHES = "No entries match these filters";

type User = ReturnType<typeof userEvent.setup>;
const press = (user: User, name: string) => user.click(screen.getByRole("button", { name }));
const dropFrame = () => FakeSocket.find("/line/ws").send({ ...lineSnapshot(), type: "line", at: "2026-10-31T11:00:00.000Z", entries: [], entriesDropped: 2 });
const loseStream = async () => act(() => FakeSocket.find("/line/ws").drop());

interface Gap {
  name: string;
  line?: Reply | Handler;
  live?: Reply | Handler;
  /** The stream's first frame states `evicted` too, and it wins over the read. */
  evicted?: number;
  /** The row's own sentence, so another gap in the same setup cannot stand in for it. */
  says?: RegExp;
  then?: (user: User) => Promise<unknown>;
  notice: string | RegExp;
}

/** Every notice that says entries are missing, dropped, evicted or unread. */
const GAPS: Gap[] = [
  {
    name: "a failed Line read",
    line: failsOnRequest(LINE),
    then: async (user) => {
      await act(async () => dropFrame());
      failingFromNow();
      await press(user, "Read the newest entries again");
    },
    notice: "Reading the logs",
    says: /production line log could not be read/,
  },
  { name: "a failed service read", live: FAILED, notice: "Reading the service logs" },
  {
    name: "service entries from the last good read",
    live: failsOnRequest(SERVICE),
    then: (user) => {
      failingFromNow();
      return press(user, "Read service entries again");
    },
    notice: /from the last good read/,
  },
  { name: "the stream lost", then: loseStream, notice: /are missing until the stream returns/ },
  {
    name: "entries still to read after the stream returned",
    then: async (user) => {
      await loseStream();
      await press(user, "Reconnect now");
    },
    notice: "Load missed entries",
  },
  { name: "entries too fast to list", then: async () => act(async () => dropFrame()), notice: /Entries came too fast/ },
  { name: "Line entries that left memory", line: lineWith({ evicted: 3 }), evicted: 3, notice: /3 older entries left memory/ },
  { name: "service entries that left memory", live: serviceWith({ evicted: 3 }), notice: /3 older service entries left memory/ },
  { name: "service entries past the limit", live: serviceWith({ matched: 4, droppedByLimit: 4 }), notice: "Showing 0 of 4 matching service entries and browser reports, the newest of each kind." },
  { name: "browser reports that left memory", live: serviceWith({ browserEvicted: 3 }), notice: /3 older browser reports left memory/ },
];

/** Depends on the filters, so it cannot run under the paths the table above runs on. */
const NOTHING_READ = "No log was read:";

/** Every other notice, and why it leaves the empty state's claim true. */
const CLAIMS_STAND: { name: string; path: string; notice: RegExp; claim: string }[] = [
  { name: "service entries do not update live: a fact about the read, nothing is missing", path: "/logs", notice: /do not update live/, claim: NOTHING_LOGGED },
  { name: "no service entry names this farm: the filter's own answer", path: "/logs?farm=farm-a", notice: /No service entry names this farm or batch/, claim: NOTHING_MATCHES },
  { name: "a filter no service entry can match: nothing unread could match", path: "/logs?level=info", notice: /Only the production line log was read/, claim: NOTHING_MATCHES },
];

/** Neither says anything is missing: one is a control, one the note that names what its read covers. */
const NOT_A_CLAIM = ["Read service entries again"];
/** Only shown beside rows, so never beside an empty list. */
const BESIDE_ROWS_ONLY = ["The production line log could not be read. Showing service entries only.", "Showing production line entries only."];
const HELPER_THAT_DRAWS_NOTES = 1;

const noticesIn = (source: string) => source.match(/<\w*Banner\b|<Button\b|<Text\b|\bnote\(["`]/g)?.length ?? 0;

/**
 * A new file here could hold a notice the count below never reads.
 * The limit: a notice with another component name, or added to a listed file other than the two notice files, gets past this.
 */
const PAGE_FILES = ["./EntriesNotices.tsx", "./LogEntries.tsx", "./LogFilterFields.tsx", "./LogsPage.tsx", "./ReasonCountsPanel.tsx", "./ServiceNotices.tsx", "./logColumns.tsx"];
const pageFiles = Object.keys(import.meta.glob(["./*.tsx", "!./*.test.tsx"]));

async function open({ line = LINE, live = SERVICE, evicted = 0, then }: Gap, path: string) {
  const user = userEvent.setup();
  installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": line, "GET /live/logs": live });
  FakeSocket.greet("/line/ws", lineSnapshot({ evicted }));
  renderApp(path);
  await screen.findByText(/No entries to show|Nothing logged yet|No entries match these filters/);
  await then?.(user);
}

beforeEach(() => {
  readsFail = false;
  useFakeSockets();
});
afterEach(() => void vi.unstubAllGlobals());

describe("REQ-BT-052 an empty list and what stands beside it", () => {
  describe.each(["/logs", "/logs?level=error"])("%s", (path) => {
    it.each(GAPS)("REQ-BT-052 $name: no claim that nothing is there", async (gap) => {
      await open(gap, path);
      expect((await screen.findAllByText(gap.notice)).length).toBeGreaterThan(0);
      expect(await screen.findByText(gap.says ?? /this list may be missing entries/)).toBeInTheDocument();
      expect(screen.getByText(/this list may be missing entries/)).toBeInTheDocument();
      expect(screen.queryByText(NOTHING_LOGGED)).not.toBeInTheDocument();
      expect(screen.queryByText(NOTHING_MATCHES)).not.toBeInTheDocument();
    });
  });

  it("REQ-BT-052 Line entries past the limit: no claim that nothing is there, with no notice of its own", async () => {
    await open({ name: "", line: lineWith({ matched: 4, droppedByLimit: 4 }), notice: "" }, "/logs");
    expect(await screen.findByText(/this list may be missing entries/)).toBeInTheDocument();
    expect(screen.queryByText(NOTHING_LOGGED)).not.toBeInTheDocument();
  });

  it("REQ-BT-052 a counter above the list: no claim that nothing matches, and no guessed cause", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": LINE, "GET /live/logs": SERVICE });
    FakeSocket.greet("/line/ws", lineSnapshot({ status: { ...LINE_STOPPED, errors: 3 } }));
    renderApp("/logs?level=error");
    expect(await screen.findByText("The Dashboard says 3 errors and this log holds 0.")).toBeInTheDocument();
    expect(await screen.findByText(/more errors than this log holds.*this list may be missing entries/)).toBeInTheDocument();
    expect(screen.queryByText(NOTHING_MATCHES)).not.toBeInTheDocument();
  });

  it.each(CLAIMS_STAND)("REQ-BT-052 $name", async ({ path, notice, claim }) => {
    await open({ name: "", notice: "" }, path);
    expect(await screen.findByText(notice)).toBeInTheDocument();
    expect(await screen.findByText(claim)).toBeInTheDocument();
  });

  it("REQ-BT-052 while the stream is lost it says so, not that the outage is over", async () => {
    await open({ name: "", then: loseStream, notice: "" }, "/logs");
    expect(await screen.findByText(/Live updates are lost, so newer entries are missing/)).toBeInTheDocument();
    expect(screen.queryByText(/Entries from the outage/)).not.toBeInTheDocument();
  });

  it.each(["/logs?reason=browser-failure&source=line", "/logs?reason=browser-failure&level=info"])("B-067 %s: no log read, so no claim that nothing matches", async (path) => {
    await open({ name: "", notice: "" }, path);
    expect(screen.getByText(new RegExp(`^${NOTHING_READ}`))).toBeInTheDocument();
    expect(screen.getByText(/this list may be missing entries/)).toBeInTheDocument();
    expect(screen.queryByText(NOTHING_MATCHES)).not.toBeInTheDocument();
  });

  it("REQ-BT-052 two gaps are both said", async () => {
    await open({ name: "", line: lineWith({ evicted: 3 }), evicted: 3, live: serviceWith({ evicted: 2 }), notice: "" }, "/logs");
    expect(await screen.findByText(/older Line entries left memory.*older service entries left memory.*this list may be missing entries/i)).toBeInTheDocument();
  });

  it("REQ-BT-052 rows on screen keep the table, whatever stands beside them", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": lineWith({ evicted: 3 }), "GET /live/logs": { body: serviceLogs({ entries: [serviceEntry()] }) } });
    FakeSocket.greet("/line/ws", lineSnapshot());
    renderApp("/logs");
    expect(await screen.findByRole("table", { name: "Log entries" })).toBeInTheDocument();
    expect(screen.queryByText(/this list may be missing entries/)).not.toBeInTheDocument();
  });

  it("REQ-BT-052 a notice added to the page must join one of these tables", () => {
    const named = GAPS.length + 1 + CLAIMS_STAND.length + NOT_A_CLAIM.length + BESIDE_ROWS_ONLY.length + HELPER_THAT_DRAWS_NOTES;
    expect(noticesIn(entriesNotices) + noticesIn(serviceNotices)).toBe(named);
    expect(pageFiles.sort()).toEqual(PAGE_FILES);
  });
});
