import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { postBrowserReport } from "../../../api/client";
import { startBrowserReports } from "../../../api/reports";
import { LINE_STOPPED, lineLogs, lineSnapshot, serviceEntry, serviceLogs } from "../../../mocks/fixtures";
import { FakeSocket, useFakeSockets, type Reply } from "../../../mocks/harness";
import { CORRELATION, ID, INGRESS, LINE, LIVE, NOT_FOLLOWED, PAGE, SERVED, found, ingress, open, reads, report, trail } from "../../../mocks/trail";

const ANY_CLAIM = /never arrived|stopped at the ingress|may have been lost/;
const LINE_EVICTED = { [LINE]: { body: lineLogs({ matched: 0, entries: [], byReason: {}, evicted: 3 }) } };

async function reading(pattern: string | RegExp) {
  return within(await trail()).findByText(pattern);
}

beforeEach(() => useFakeSockets());
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("B-065 a row says what it shows and no more", () => {
  it.each([200, 404, 502, 504])("B-065 a %i row reads as arrived with that status, never stopped", async (status) => {
    open({ [INGRESS]: ingress([{ ...SERVED, status }]) });
    expect(await reading(`A request with this ID arrived at the ingress, last with status ${status}.`)).toBeInTheDocument();
    expect(await trail()).not.toHaveTextContent("stopped");
  });

  it("B-065 the newest row gives the status", async () => {
    open({ [INGRESS]: ingress([{ ...SERVED, status: 503 }, { ...SERVED, at: "2026-10-31T08:59:58.000Z", status: 200 }]) });
    expect(await reading(/last with status 503\.$/)).toBeInTheDocument();
  });

  it.each([
    ["a failed Line read", { [LINE]: { status: 400, body: { title: "No" } } }],
    ["evicted service entries", { [LIVE]: { body: serviceLogs({ evicted: 4, entries: [report()] }) } }],
    ["evicted Line entries", LINE_EVICTED],
  ])("B-065 a row stays what it is beside %s", async (_name, routes) => {
    open({ [INGRESS]: ingress([SERVED]), ...routes });
    expect(await reading(/^A request with this ID arrived/)).toBeInTheDocument();
  });

  it("REQ-BT-056 a row and a service row: it reached a service", async () => {
    open({ [INGRESS]: ingress([SERVED]), [LIVE]: found(report(), serviceEntry({ correlationId: CORRELATION })) });
    expect(await reading("The request reached a service.")).toBeInTheDocument();
  });

  it("REQ-BT-056 a Line row counts as reaching a service too", async () => {
    open({ [INGRESS]: ingress([SERVED]), [LINE]: { body: lineLogs({ matched: 1 }) } });
    expect(await reading("The request reached a service.")).toBeInTheDocument();
  });

  it("REQ-BT-056 the report's own row is no sign the request reached a service", async () => {
    open({ [INGRESS]: ingress([SERVED]), [LIVE]: found(report()) });
    expect(await reading(/^A request with this ID arrived/)).toBeInTheDocument();
  });

  it("REQ-BT-056 says nothing until the Line has answered, so a slow Line cannot flip the sentence", async () => {
    let answer: (reply: Reply) => void = () => undefined;
    const slow = new Promise<Reply>((resolve) => (answer = resolve));
    open({ [INGRESS]: ingress([SERVED]), [LINE]: () => slow });
    const panel = await trail();
    await within(panel).findByRole("table", { name: "Ingress rows" });
    expect(panel).not.toHaveTextContent("arrived at the ingress");
    await act(async () => answer({ body: lineLogs({ matched: 1 }) }));
    expect(await reading("The request reached a service.")).toBeInTheDocument();
  });
});

describe("B-065 no row at the ingress", () => {
  it("REQ-BT-056 every log read in full, the failure inside the log: it never arrived", async () => {
    open({});
    expect(await reading("The request never arrived at the ingress.")).toBeInTheDocument();
  });

  it.each([
    ["a failed Line read", { [LINE]: { status: 400, body: { title: "No" } } }],
    ["evicted service entries", { [LIVE]: { body: serviceLogs({ evicted: 4, entries: [report()] }) } }],
    ["evicted Line entries", LINE_EVICTED],
    ["service entries over the limit", { [LIVE]: { body: serviceLogs({ droppedByLimit: 2, entries: [report()] }) } }],
    ["Line entries over the limit", { [LINE]: { body: lineLogs({ matched: 0, entries: [], byReason: {}, droppedByLimit: 2 }) } }],
  ])("B-065 %s never reads as never arrived", async (_name, routes) => {
    open(routes);
    expect(await reading(/^No ingress row for this ID, but not every log was read in full/)).toBeInTheDocument();
    expect(await trail()).not.toHaveTextContent("never arrived at the ingress");
  });

  it.each(["level=error", "farm=farm-a", "batch=b9", "reason=browser-failure", "from=2026-10-31T08:30:00.000Z", "to=2026-10-31T12:00:00.000Z"])("B-065 %s narrows the read, so no row proves nothing", async (filter) => {
    open({}, `${PAGE}&${filter}`);
    expect(await reading(/^No ingress row for this ID, but not every log was read in full/)).toBeInTheDocument();
  });

  it("B-065 a failed service read without a report ID proves nothing either", async () => {
    open({ [LIVE]: { status: 400, body: { title: "No" } } }, `/logs?correlationId=${CORRELATION}`);
    expect(await reading(/^No ingress row for this ID, but not every log was read in full/)).toBeInTheDocument();
  });

  it("B-065 leaving out the service log narrows the read too", async () => {
    open({ [LIVE]: found() }, `/logs?correlationId=${CORRELATION}&source=line`);
    expect(await reading(/^No ingress row for this ID, but not every log was read in full/)).toBeInTheDocument();
  });

  it("B-065 narrowed without a report ID too", async () => {
    open({ [LIVE]: found(report()) }, `/logs?correlationId=${CORRELATION}&level=error`);
    expect(await reading(/^No ingress row for this ID/)).toBeInTheDocument();
    expect(await trail()).not.toHaveTextContent("never arrived");
  });

  it("REQ-BT-056 a service row with no ingress row came by another way", async () => {
    open({ [LIVE]: found(report(), serviceEntry({ correlationId: CORRELATION })) });
    expect(await reading(/came by another way/)).toBeInTheDocument();
  });

  it("REQ-BT-056 a correlation ID with no failure time cannot say it never arrived", async () => {
    open({ [LIVE]: found() }, `/logs?correlationId=${CORRELATION}`);
    expect(await reading(/^No ingress row for this ID, and no failure time/)).toBeInTheDocument();
  });

  it("REQ-BT-056 a log that is not being followed reads unknown, with one read, no report and no alarm", async () => {
    const stop = startBrowserReports(postBrowserReport);
    const calls = open({ [INGRESS]: NOT_FOLLOWED, "POST /api/browser-reports": { status: 202 } });
    const panel = await trail();
    expect(await within(panel).findByText(/^Unknown: the ingress log is not being followed/)).toBeInTheDocument();
    expect(panel).not.toHaveTextContent(ANY_CLAIM);
    expect(reads(calls, "/ingress/logs")).toHaveLength(1);
    expect(calls.filter((call) => call.method === "POST")).toHaveLength(0);
    expect(within(panel).queryByRole("alert")).not.toBeInTheDocument();
    stop();
  });

  it("REQ-BT-056 a failed ingress read reads unknown and offers a retry", async () => {
    open({ [INGRESS]: { status: 400, body: { title: "Bad", detail: "Bad id" } } });
    const panel = await trail();
    expect(await within(panel).findByText("Bad id")).toBeInTheDocument();
    expect(within(panel).getByText(/^Unknown: the ingress log could not be read/)).toBeInTheDocument();
    expect(panel).not.toHaveTextContent(ANY_CLAIM);
  });
});

describe("B-065 the report's time is not the request's", () => {
  it("REQ-BT-056 a failure older than the log's start is too old to tell", async () => {
    open({ [INGRESS]: ingress([], "2026-10-31T10:00:00.000Z") });
    expect(await reading(/^Too old to tell/)).toBeInTheDocument();
  });

  it("REQ-BT-056 a null start is too old to tell", async () => {
    open({ [INGRESS]: ingress([], null) });
    expect(await reading(/^Too old to tell/)).toBeInTheDocument();
  });

  it("B-065 a log starting just before the report may still miss the request behind it", async () => {
    open({ [INGRESS]: ingress([], "2026-10-31T08:59:00.000Z") });
    expect(await reading(/^Too old to tell/)).toBeInTheDocument();
    expect(await trail()).not.toHaveTextContent("never arrived");
  });

  it("B-065 past the longest a request can run, the log does reach back", async () => {
    open({ [INGRESS]: ingress([], "2026-10-31T08:40:00.000Z") });
    expect(await reading("The request never arrived at the ingress.")).toBeInTheDocument();
  });
});

describe("B-065 a report that was not found", () => {
  it("REQ-BT-056 a full read of the service log says it may be lost", async () => {
    const calls = open({ [LIVE]: found() });
    expect(await reading(/^No report with this ID is in the service log/)).toBeInTheDocument();
    expect(reads(calls, "/ingress/logs")).toHaveLength(0);
  });

  it(`B-076 a different correlation ID may be hiding it, since the service log applies both`, async () => {
    open({ [LIVE]: found() }, `${PAGE}&correlationId=${"cd".repeat(16)}`);
    expect(await reading(/^Unknown: these filters may hide the report/)).toBeInTheDocument();
    expect(await trail()).not.toHaveTextContent(ANY_CLAIM);
  });

  it("B-076 a report's own correlation ID hides nothing", async () => {
    open({}, `${PAGE}&correlationId=${CORRELATION}`);
    expect(await reading("The request never arrived at the ingress.")).toBeInTheDocument();
  });

  it.each(["batch=b9", "farm=farm-a", "level=error"])("B-065 %s may be hiding it, which is no missing report", async (filter) => {
    open({ [LIVE]: found() }, `${PAGE}&${filter}`);
    expect(await reading(/^Unknown: these filters may hide the report/)).toBeInTheDocument();
    expect(await trail()).not.toHaveTextContent(ANY_CLAIM);
  });

  it("B-065 browser reports that left memory may have taken it", async () => {
    open({ [LIVE]: { body: serviceLogs({ matched: 0, browserEvicted: 7, entries: [] }) } });
    expect(await reading(/^Unknown: older browser reports left memory/)).toBeInTheDocument();
    expect(await trail()).not.toHaveTextContent(ANY_CLAIM);
  });

  it("B-062 service rows leaving memory took no report, so it may be lost as usual", async () => {
    open({ [LIVE]: { body: serviceLogs({ matched: 0, evicted: 7, browserEvicted: 0, entries: [] }) } });
    expect(await reading(/^No report with this ID is in the service log/)).toBeInTheDocument();
  });

  it("B-062 the notice names browser reports apart from service entries", async () => {
    open({ [LIVE]: { body: serviceLogs({ matched: 0, browserEvicted: 7, entries: [] }) } });
    expect(await screen.findByText("7 older browser reports left memory")).toBeInTheDocument();
    expect(screen.queryByText(/older service entries left memory/)).not.toBeInTheDocument();
  });

  it("B-062 an empty list says browser reports may be missing from it", async () => {
    open({ [LIVE]: { body: serviceLogs({ matched: 0, browserEvicted: 7, entries: [] }) } }, "/logs");
    expect(await screen.findByText(/Older browser reports left memory\./)).toBeInTheDocument();
  });

  it("B-062 browser rows leaving memory say nothing about where a request went", async () => {
    open({ [LIVE]: { body: serviceLogs({ browserEvicted: 7, entries: [report()] }) } });
    expect(await reading("The request never arrived at the ingress.")).toBeInTheDocument();
  });

  it("REQ-BT-056 a failed service read cannot say it is lost", async () => {
    open({ [LIVE]: { status: 400, body: { title: "No" } } });
    expect(await reading(/^Unknown: the service log could not be read/)).toBeInTheDocument();
    expect(await trail()).not.toHaveTextContent(ANY_CLAIM);
  });

  it("REQ-BT-056 a filter that leaves out the service log cannot say it is lost", async () => {
    open({}, `/logs?reportId=${ID}&source=line`);
    expect(await reading(/^Unknown: these filters leave out the service log/)).toBeInTheDocument();
  });

  it("REQ-BT-056 a report with no correlation ID has nothing to match on, so neither Line nor ingress is read", async () => {
    const calls = open({ [LIVE]: found(report({ correlationId: null })) });
    expect(await reading("This report has no correlation ID, so there is nothing to match on.")).toBeInTheDocument();
    expect(reads(calls, "/ingress/logs")).toHaveLength(0);
    expect(reads(calls, "/line/logs")).toHaveLength(0);
    expect(screen.queryByRole("region", { name: "Count by reason" })).not.toBeInTheDocument();
  });

  it("REQ-BT-056 Line frames stay off a report that has nothing to match on", async () => {
    open({ [LIVE]: found(report({ correlationId: null })) });
    await trail();
    await act(async () => FakeSocket.find("/line/ws").send(lineSnapshot({ status: LINE_STOPPED, entries: lineLogs({ matched: 1 }).entries })));
    expect(within(await screen.findByRole("table", { name: "Log entries" })).getAllByRole("row")).toHaveLength(2);
  });
});

describe("B-067 filters that leave no log to read", () => {
  const NOTHING_READ = /^No log was read:/;

  it.each(["source=line", "level=info"])("B-067 browser-failure with %s says so, and never that the Line was read", async (filter) => {
    const calls = open({}, `/logs?reason=browser-failure&${filter}`);
    expect(await screen.findByText(NOTHING_READ)).toBeInTheDocument();
    expect(screen.queryByText(/Only the production line log was read/)).not.toBeInTheDocument();
    expect(reads(calls, "/line/logs")).toHaveLength(0);
    expect(reads(calls, "/live/logs")).toHaveLength(0);
  });

  it("B-067 picking the reason keeps Source, so a click gets here too", async () => {
    open({}, "/logs?source=line");
    await userEvent.selectOptions(await screen.findByRole("combobox", { name: "Reason" }), "browser-failure");
    expect(screen.getByRole("combobox", { name: "Source" })).toHaveValue("line");
    expect(await screen.findByText(NOTHING_READ)).toBeInTheDocument();
  });

  it("B-067 a report ID with Level on Information reads no log either, until the report is known", async () => {
    open({}, `/logs?reportId=${ID}&level=info`);
    expect(await screen.findByText(NOTHING_READ)).toBeInTheDocument();
    expect(screen.queryByText(/Only the production line log was read/)).not.toBeInTheDocument();
  });

  it("B-076 a Line reason with a report ID names the control that keeps the service log out", async () => {
    open({}, `/logs?reportId=${ID}&reason=event-timeout`);
    expect(await screen.findByText("No log was read: a report is only in the service log, which these filters leave out. Change Reason.")).toBeInTheDocument();
  });

  it("B-076 each control that keeps the service log out is named", async () => {
    open({}, `/logs?reportId=${ID}&reason=event-timeout&source=line&level=info`);
    expect(await screen.findByText("No log was read: a report is only in the service log, which these filters leave out. Change Source, Level or Reason.")).toBeInTheDocument();
  });

  it("B-076 a browser reason names what it needs", async () => {
    open({}, "/logs?reason=browser-failure&level=info");
    expect(await screen.findByText("No log was read: a browser report is only in the service log, which these filters leave out. Change Level.")).toBeInTheDocument();
  });

  it("B-076 a correlation ID beside the report still reads the Line, so the page must not say nothing was read", async () => {
    const calls = open({}, `/logs?reportId=${ID}&correlationId=${CORRELATION}&level=info`);
    await screen.findByRole("heading", { name: "Logs" });
    await waitFor(() => expect(reads(calls, "/line/logs").length).toBeGreaterThan(0));
    expect(screen.queryByText(/^No log was read:/)).not.toBeInTheDocument();
  });

  it("REQ-BT-052 a Line reason with an all-sources filter still names the one log it read", async () => {
    open({}, "/logs?reason=event-timeout");
    expect(await screen.findByText(/Only the production line log was read/)).toBeInTheDocument();
  });
});
