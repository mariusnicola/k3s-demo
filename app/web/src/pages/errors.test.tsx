import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { client } from "../api/client";
import { MONITOR_WS } from "../config";
import { alert, batch, hop, hopSnapshot, lineLogs, lineSnapshot, logEntry, pending, REPORT } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../mocks/harness";

const ORDER = ["What happened", "Where", "What it did", "What to do next", "Correlation ID"];
const labels = (root: Element) => [...root.querySelectorAll(".ds-error-detail dt")].map((d) => d.textContent);
const refused = (id = "corr-1") => ({ status: 412, body: { title: "Precondition failed", correlationId: id } });
const BATCH = "GET /api/batches/farm-a/b-1";
const ADVANCE = "POST /api/batches/farm-a/b-1/advance";

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

async function failAdvance(user: "olivia" | "ada" = "ada") {
  const calls = installApi({ "GET /api/me": meRoute(user), [BATCH]: { etag: '"v1"', body: batch() }, [ADVANCE]: refused() });
  renderApp("/batches/farm-a/b-1");
  await userEvent.click(await screen.findByRole("button", { name: "Move to Dose 2" }));
  return { calls, banner: (await screen.findByText("Someone else changed this batch")).closest(".ds-banner")! };
}

describe("REQ-BT-052 correlation ID on the wire", () => {
  it("REQ-BT-052 each action sends its own generated ID", async () => {
    const calls = installApi({ [ADVANCE]: { body: batch() } });
    await client.advance("farm-a", "b-1", "Dose2", '"v1"');
    await client.advance("farm-a", "b-1", "Dose2", '"v1"');
    const [first, second] = calls.map((c) => c.headers["X-Correlation-Id"]);
    expect(first).toMatch(/^[0-9a-f]{32}$/);
    expect(second).toMatch(/^[0-9a-f]{32}$/);
    expect(first).not.toBe(second);
  });

  it("REQ-BT-052 a retried read keeps the ID of its action", async () => {
    let tries = 0;
    const calls = installApi({ [BATCH]: () => (++tries === 1 ? { status: 503 } : { etag: '"v1"', body: batch() }) });
    await client.getBatch("farm-a", "b-1");
    expect(calls).toHaveLength(2);
    expect(calls[1]!.headers["X-Correlation-Id"]).toBe(calls[0]!.headers["X-Correlation-Id"]);
  });

  it("REQ-BT-052 the ID still generates where randomUUID does not exist, on plain http", async () => {
    vi.stubGlobal("crypto", { getRandomValues: (a: Uint8Array) => a.fill(171) });
    const calls = installApi({ [ADVANCE]: { body: batch() } });
    await client.advance("farm-a", "b-1", "Dose2", '"v1"');
    expect(calls[0]!.headers["X-Correlation-Id"]).toBe("ab".repeat(16));
  });

});

describe("REQ-BT-053 problem banner", () => {
  it("REQ-BT-053 reads in the fixed order, with the ID the server returned", async () => {
    const { banner } = await failAdvance();
    expect(labels(banner)).toEqual(ORDER);
    expect(within(banner as HTMLElement).getByText("corr-1")).toBeInTheDocument();
  });

  it("REQ-BT-053 links to the logs filtered on that ID", async () => {
    const { banner } = await failAdvance();
    expect(within(banner as HTMLElement).getByRole("link", { name: "Open corr-1 in Logs" })).toHaveAttribute("href", "/logs?correlationId=corr-1");
    expect(within(banner as HTMLElement).getByRole("link", { name: "Open corr-1 in Logs" })).not.toHaveAttribute("aria-label");
  });

  it("REQ-BT-053 a user who cannot open the logs gets the ID and no dead link", async () => {
    const { banner } = await failAdvance("olivia");
    expect(within(banner as HTMLElement).getByText("corr-1")).toBeInTheDocument();
    expect(within(banner as HTMLElement).queryByRole("link", { name: /in Logs/ })).toBeNull();
  });

  it("REQ-BT-052 the copy control copies the ID and says so", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    const { banner } = await failAdvance();
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    await userEvent.click(within(banner as HTMLElement).getByRole("button", { name: "Copy correlation ID" }));
    expect(writeText).toHaveBeenCalledWith("corr-1");
    expect(await within(banner as HTMLElement).findByText("Copied")).toBeInTheDocument();
  });

  it("REQ-BT-052 copy works on plain http, where the page has no clipboard", async () => {
    const { banner } = await failAdvance();
    const command = vi.fn().mockReturnValue(true);
    document.execCommand = command;
    vi.stubGlobal("navigator", { ...navigator, clipboard: undefined });
    await userEvent.click(within(banner as HTMLElement).getByRole("button", { name: "Copy correlation ID" }));
    expect(command).toHaveBeenCalledWith("copy");
    expect(await within(banner as HTMLElement).findByText("Copied")).toBeInTheDocument();
  });

  it("REQ-BT-052 a copy that fails says to select the ID", async () => {
    const { banner } = await failAdvance();
    document.execCommand = vi.fn().mockReturnValue(false);
    vi.stubGlobal("navigator", { ...navigator, clipboard: undefined });
    await userEvent.click(within(banner as HTMLElement).getByRole("button", { name: "Copy correlation ID" }));
    expect(await within(banner as HTMLElement).findByText("Could not copy. Select the ID.")).toBeInTheDocument();
  });
});

describe("REQ-BT-053 logs", () => {
  const open = (path: string, entries = [logEntry({ correlationId: "corr-9", impact: "The batch stayed at Dose 1.", nextAction: "Replay the event.", exceptionStack: "at Line.Run()" })]) => {
    const calls = installApi({ "GET /api/me": meRoute("ada"), "GET /line/logs": { body: lineLogs({ entries }) } });
    FakeSocket.greet("/line/ws", lineSnapshot());
    renderApp(path);
    return calls;
  };
  const expand = async () => {
    await userEvent.click(await screen.findByRole("button", { name: /details of/ }));
    return document.querySelector(".ds-error-detail")!.parentElement!;
  };

  it("REQ-BT-053 an entry reads in the fixed order", async () => {
    open("/logs");
    const detail = await expand();
    expect(labels(detail)).toEqual(ORDER);
    expect(detail).toHaveTextContent("The batch stayed at Dose 1.");
    expect(detail).toHaveTextContent("Replay the event.");
  });

  it("REQ-BT-053 exception and stack are folded, closed, and reachable by keyboard", async () => {
    open("/logs");
    const detail = await expand();
    const folded = detail.querySelector("details")!;
    expect(folded).not.toHaveAttribute("open");
    expect(folded).toHaveTextContent("at Line.Run()");
    expect(folded).toHaveTextContent("TaskCanceledException");
    const summary = within(folded).getByText("Technical detail");
    expect(summary.tagName).toBe("SUMMARY");
    expect(detail.querySelector(".ds-error-detail")).not.toHaveTextContent("TaskCanceledException");
  });

  it("REQ-BT-053 fields the service did not send leave no empty label", async () => {
    open("/logs", [logEntry({ reason: "closed" })]);
    const detail = await expand();
    expect(labels(detail)).toEqual(["What happened", "Where"]);
  });

  it("REQ-BT-052 the correlation filter comes from the address and goes to the service", async () => {
    const calls = open("/logs?correlationId=corr-9");
    expect(await screen.findByRole("textbox", { name: /Correlation ID/ })).toHaveValue("corr-9");
    await waitFor(() => expect(calls.find((c) => c.url.includes("/line/logs"))!.url).toContain("correlationId=corr-9"));
  });

  it("REQ-BT-052 typing an ID puts it in the address, whatever it holds", async () => {
    const calls = open("/logs?correlationId=%3Cany%20text%3E");
    const field = await screen.findByRole("textbox", { name: /Correlation ID/ });
    expect(field).toHaveValue("<any text>");
    await userEvent.clear(field);
    await userEvent.type(field, "corr-7{Enter}");
    await waitFor(() => expect(calls.at(-1)!.url).toContain("correlationId=corr-7"));
  });

  it("REQ-BT-052 a live entry for another ID stays out of a filtered list", async () => {
    open("/logs?correlationId=corr-9");
    await screen.findByText(logEntry().message);
    const other = logEntry({ at: "2026-10-31T10:01:00.000Z", message: "Another ID", correlationId: "corr-1" });
    act(() => FakeSocket.find("/line/ws").send({ type: "line", at: other.at, status: lineSnapshot().status, entries: [other], entriesDropped: 0, evicted: 0 }));
    await act(async () => undefined);
    expect(screen.queryByText("Another ID")).toBeNull();
  });
});

describe("REQ-BT-053 alerts", () => {
  const open = async (item = alert()) => {
    installApi({ "GET /api/me": meRoute("ada") });
    FakeSocket.greet("/ws/hops", hopSnapshot({ alerts: [item] }));
    renderApp("/alerts");
    await userEvent.click(await screen.findByRole("button", { name: /details of/ }));
    return document.querySelector(".ds-error-detail")!;
  };

  it("REQ-BT-053 a blocked alert says what it did and what to do", async () => {
    const detail = await open();
    expect(labels(detail.parentElement!)).toEqual(ORDER.slice(0, 4));
    expect(detail).toHaveTextContent("Bovine vaccine on farm-a");
    expect(detail).toHaveTextContent("The batch stays Blocked until the event is replayed.");
  });

  it("REQ-BT-053 an operator is told who can replay", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    FakeSocket.greet("/ws/hops", hopSnapshot({ alerts: [alert({ type: "Blocked" })] }));
    renderApp("/alerts");
    await userEvent.click(await screen.findByRole("button", { name: /details of/ }));
    expect(document.querySelector(".ds-error-detail")).toHaveTextContent("Ask an admin to replay the event.");
  });

  it("REQ-BT-052 an alert that carries an ID shows it", async () => {
    const detail = await open(alert({ correlationId: "corr-4" }));
    expect(labels(detail.parentElement!)).toEqual(ORDER);
    expect(within(detail as HTMLElement).getByRole("link", { name: "Open corr-4 in Logs" })).toBeInTheDocument();
  });
});

describe("REQ-BT-053 batch detail", () => {
  it("REQ-BT-053 a blocked batch reads in the fixed order, with the raw error folded", async () => {
    installApi({ "GET /api/me": meRoute("ada"), [BATCH]: { etag: '"v1"', body: batch({ pending: pending("dead", null, "Poison message") }) } });
    renderApp("/batches/farm-a/b-1");
    const banner = (await screen.findByText("Blocked: the compliance event failed")).closest(".ds-banner")!;
    expect(labels(banner)).toEqual(ORDER.slice(0, 4));
    expect(banner.querySelector(".ds-error-detail")).not.toHaveTextContent("Poison message");
    expect(banner.querySelector("details")).toHaveTextContent("Poison message");
  });
});

describe("REQ-BT-053 who can act", () => {
  it("REQ-BT-053 an operator is told who can replay, not to replay", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), [BATCH]: { etag: '"v1"', body: batch({ pending: pending("dead", null, "Poison message") }) } });
    renderApp("/batches/farm-a/b-1");
    const banner = (await screen.findByText("Blocked: the compliance event failed")).closest(".ds-banner")!;
    expect(banner.querySelector(".ds-error-detail")).toHaveTextContent("Ask an admin to replay the event.");
  });
});

describe("REQ-BT-053 cluster operations", () => {
  it("REQ-BT-053 a failed operation says what happened and what to do", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /api/batches": { body: [] } });
    FakeSocket.greet(MONITOR_WS, { type: "operations", operations: [{ id: "o1", name: "Deploy", step: "Charts", startedAt: "2026-10-31T10:00:00Z", lastSeenAt: "2026-10-31T10:01:00Z", state: "failed", endedAt: null, correlationId: "corr-6" }] });
    renderApp("/batches");
    const banner = (await screen.findByText("Deploy failed")).closest(".ds-operation")!;
    expect(labels(banner)).toEqual(["What happened", "Where", "What to do next", "Correlation ID"]);
    expect(banner).toHaveTextContent("Stopped at step Charts");
  });
});

describe("REQ-BT-052 dashboard", () => {
  it("REQ-BT-052 a hop that carries an ID links to its log entries", async () => {
    installApi({ "GET /api/me": meRoute("ada") });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    FakeSocket.greet("/line/ws", lineSnapshot());
    renderApp("/dashboard");
    await screen.findByRole("heading", { name: "Architecture" });
    act(() => FakeSocket.find("/ws/hops").sendHops([hop({ ok: false, correlationId: "corr-3" }), hop({ traceId: "trace-2" })]));
    const table = await screen.findByRole("table", { name: "Recent hops" });
    expect(await within(table).findByRole("link", { name: "Open corr-3 in Logs" })).toHaveAttribute("href", "/logs?correlationId=corr-3");
    expect(within(table).getAllByText("None").length).toBeGreaterThan(0);
  });

  it("REQ-BT-053 failed pods read in the order too", async () => {
    installApi({ "GET /api/me": meRoute("ada") });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    FakeSocket.greet("/line/ws", lineSnapshot());
    renderApp("/dashboard");
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() => socket.send({ ...REPORT, checkedAt: new Date().toISOString(), pods: [{ ...REPORT.pods![0]!, name: "job-1", phase: "Failed", ready: false }] }));
    const banner = (await screen.findByText("1 pod failed")).closest(".ds-banner")!;
    expect(labels(banner)).toEqual(["What happened", "Where", "What to do next"]);
    expect(banner).toHaveTextContent("job-1");
  });
});
