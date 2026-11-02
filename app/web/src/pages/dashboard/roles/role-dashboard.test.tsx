import { act, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Batch, BatchState, Me } from "../../../api/types";
import { MONITOR_WS } from "../../../config";
import { batch, hopSnapshot, lineSnapshot, pending, step, USERS } from "../../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../../../mocks/harness";

const NOW = new Date(2026, 9, 31, 12, 0, 0).getTime();
const TODAY = new Date(NOW - 3_600_000).toISOString();
const YESTERDAY = new Date(NOW - 48 * 3_600_000).toISOString();
type Role = "olivia" | "ian" | "viewer" | "ada";

const at = (state: BatchState, when = TODAY) => step(state, true, { at: when });
const made = (id: string, state: BatchState, over: Partial<Batch> = {}) => batch({ id, state, product: `Product ${id}`, createdAt: TODAY, steps: [at(state)], ...over });

/** What the real endpoints do: the same permission on each route, the same farm scope, newest created first, capped by limit. */
function server(user: Role, rows: Batch[]) {
  const me: Me = USERS[user]!;
  const reachable = (b: Batch) => me.farms.includes("*") || me.farms.includes(b.farmId);
  return {
    "GET /api/me": meRoute(user),
    "GET /api/batches": (url: string) => {
      const query = new URL(url, "http://x").searchParams;
      const farm = query.get("farmId");
      if (farm && !me.farms.includes("*") && !me.farms.includes(farm)) return { status: 403 };
      const body = rows
        .filter((b) => reachable(b) && (!query.get("state") || b.state === query.get("state")) && (!farm || b.farmId === farm))
        .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
        .slice(0, Number(query.get("limit") ?? 50));
      return { body };
    },
  };
}

/** Everything a role may not reach. A call here is the 403 this requirement forbids. */
function refused(user: Role, call: { method: string; url: string }): boolean {
  if (USERS[user]!.permissions.includes("ops.admin")) return false;
  const adminOnly = ["/line/", "/line?", "/metrics/", "/compliance/", "/fail-next", "/replay"].some((part) => call.url.includes(part)) || call.url.endsWith("/line");
  const farm = new URL(call.url, "http://x").searchParams.get("farmId");
  const outside = farm !== null && !USERS[user]!.farms.includes("*") && !USERS[user]!.farms.includes(farm);
  return adminOnly || outside || call.method !== "GET";
}

function open(user: Role, rows: Batch[] = [], summary: Record<string, unknown> = {}, path = "/dashboard") {
  const calls = installApi(server(user, rows));
  FakeSocket.greet("/ws/hops", hopSnapshot({ summary: { byState: {}, pending: 0, blocked: 0, flagged: 0, ...summary } }));
  FakeSocket.greet("/line/ws", lineSnapshot());
  renderApp(path);
  return calls;
}

const stat = (label: string) => [...document.querySelectorAll(".ds-stat")].find((s) => s.textContent?.includes(label))!;
const nav = () => screen.getByRole("navigation");

beforeEach(() => {
  useFakeSockets();
  vi.spyOn(Date, "now").mockReturnValue(NOW);
});
afterEach(() => void vi.restoreAllMocks());

describe("REQ-BT-055 the dashboard follows the role", () => {
  const order = () => [...document.querySelectorAll("main h2")].map((h) => h.textContent);

  it("REQ-BT-055 olivia's own queue comes first", async () => {
    open("olivia", [made("a", "Dose1")]);
    await screen.findByRole("link", { name: "Product a" });
    expect(order()[0]).toBe("Waiting for your next step");
    expect(order().indexOf("Batches by step")).toBeGreaterThan(0);
  });
  it("REQ-BT-055 ian's inspection queue comes first", async () => {
    open("ian", [made("a", "Dose2")]);
    await screen.findByRole("link", { name: /Product a/ });
    expect(order()[0]).toBe("Waiting for inspection");
    expect(order().indexOf("Batches by step")).toBeGreaterThan(0);
  });
  it("REQ-BT-055 viewer reads their farm's state first, with no action to take", async () => {
    open("viewer", [made("a", "Dose1"), made("b", "Closed")], { byState: { Dose1: 4, Closed: 7, Rejected: 2 }, blocked: 1, flagged: 3 });
    await waitFor(() => expect(stat("Open batches")).toHaveTextContent("4"));
    expect(stat("Blocked")).toHaveTextContent("1");
    expect(stat("Blocked")).toHaveAttribute("href", "/alerts");
    expect(stat("Flagged")).toHaveTextContent("3");
    expect(stat("Closed in total")).toHaveTextContent("7");
    expect(stat("Open batches").compareDocumentPosition(screen.getByRole("heading", { name: "Batches by step" })) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(screen.getByRole("main")).queryByRole("button", { name: /Advance|Inspect|Create|Run|Start|Move/ })).toBeNull();
  });
  it("REQ-BT-055 viewer's figures wait for the store, they never read zero", async () => {
    installApi(server("viewer", []));
    FakeSocket.greet("/ws/hops", hopSnapshot({ reconciledAt: null, summary: null }));
    renderApp("/dashboard");
    await screen.findByRole("heading", { level: 1, name: "Dashboard" });
    await waitFor(() => expect(stat("Open batches")).toHaveTextContent("Loading"));
    expect(stat("Blocked")).toHaveTextContent("Loading");
  });

  it.each<[Role, string]>([
    ["olivia", "Your work on farm-a and farm-b."],
    ["ian", "Inspection across all farms."],
    ["viewer", "Batches on farm-a. This view is read only."],
  ])("REQ-BT-055 %s gets a dashboard of their own", async (user, subtitle) => {
    open(user);
    expect(await screen.findByText(subtitle)).toBeInTheDocument();
    expect(await screen.findByRole("heading", { name: "Batches by step" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Architecture" })).toBeNull();
  });
  it("REQ-BT-055 an admin keeps the cluster dashboard", async () => {
    open("ada");
    expect(await screen.findByRole("heading", { name: "Architecture" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Recent hops" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Batches by step" })).toBeNull();
  });

  it.each<[Role, Batch[]]>([
    ["olivia", [made("a", "Dose1"), made("b", "Closed")]],
    ["ian", [made("a", "Dose2"), made("b", "Inspected")]],
    ["viewer", [made("a", "Closed")]],
    ["ada", []],
  ])("REQ-BT-055 %s never calls what their role is refused", async (user, rows) => {
    const calls = open(user, rows);
    await screen.findByRole("heading", { level: 1, name: "Dashboard" });
    await waitFor(() => expect(calls.some((c) => c.url.includes("/api/session"))).toBe(true));
    await act(async () => undefined);
    expect(calls.filter((c) => refused(user, c))).toEqual([]);
    if (user !== "ada") expect(FakeSocket.all.filter((s) => s.url.endsWith("/line/ws"))).toEqual([]);
  });

  it.each<Role>(["olivia", "ian", "viewer"])("REQ-BT-055 %s is never told to sign in as someone else", async (user) => {
    open(user);
    await screen.findByRole("heading", { name: "Batches by step" });
    expect(document.body).not.toHaveTextContent(/needs an admin|need an admin|Sign in as|lacks ops\.admin/);
  });
});

describe("REQ-BT-055 batches by step", () => {
  it("REQ-BT-055 the map shows every state with its number, also as text", async () => {
    open("olivia", [], { byState: { Created: 3, Dose1: 2, Rejected: 1 } });
    const map = await screen.findByRole("img", { name: /3 Created/ });
    expect(map).toHaveAccessibleName(expect.stringContaining("1 Rejected"));
    expect(map).toHaveAccessibleName(expect.stringContaining("0 Closed"));
    expect(map.querySelectorAll("[data-state-block]")).toHaveLength(7);
    expect(map.querySelector('[data-state-block="Created"]')).toHaveTextContent("3");
  });
  it("REQ-BT-055 a state with more batches stands taller", async () => {
    open("olivia", [], { byState: { Created: 9, Dose1: 1 } });
    const map = await screen.findByRole("img", { name: /9 Created/ });
    const height = (state: string) => Number(map.querySelector(`[data-state-block="${state}"]`)!.getAttribute("data-height"));
    expect(height("Created")).toBeGreaterThan(height("Dose1"));
    expect(height("Dose1")).toBeGreaterThan(height("Closed"));
  });

  it("REQ-BT-055 no summary yet reads as loading, never as zero", async () => {
    installApi(server("olivia", []));
    renderApp("/dashboard");
    expect(await screen.findByRole("status", { name: "Loading batches by step" })).toBeInTheDocument();
    expect(screen.queryByRole("img", { name: /0 Created/ })).toBeNull();
  });
});

describe("REQ-BT-055 operator", () => {
  it("REQ-BT-055 lists what this operator can move, with the next step, and nothing else", async () => {
    open("olivia", [
      made("ready", "Dose1"),
      made("inspect", "Dose2"),
      made("flight", "Created", { pending: pending("sent", TODAY) }),
      made("stuck", "Dose1", { pending: pending("dead") }),
      made("done", "Closed"),
    ]);
    const table = await screen.findByRole("table", { name: "Waiting for your next step" });
    expect(within(table).getByRole("link", { name: "Product ready" })).toHaveAttribute("href", "/batches/farm-a/ready");
    expect(within(table).getByText("Move to Dose 2")).toBeInTheDocument();
    for (const other of ["inspect", "flight", "stuck", "done"]) expect(within(table).queryByText(`Product ${other}`)).toBeNull();
    expect(stat("Waiting for your next step")).toHaveTextContent("1");
  });

  it("REQ-BT-055 shows blocked and rejected with a way to them", async () => {
    open("olivia", [], { blocked: 2, byState: { Rejected: 4 } });
    await screen.findByText("Blocked");
    expect(stat("Blocked")).toHaveTextContent("2");
    expect(stat("Rejected")).toHaveTextContent("4");
    expect(stat("Blocked")).toHaveAttribute("href", "/alerts");
    expect(stat("Rejected")).toHaveAttribute("href", "/batches?state=Rejected");
  });

  it("REQ-BT-055 closed today counts today only, by when it closed", async () => {
    open("olivia", [made("a", "Closed"), made("b", "Closed", { createdAt: YESTERDAY }), made("c", "Closed", { steps: [at("Closed", YESTERDAY)] })]);
    await waitFor(() => expect(stat("Closed today")).toHaveTextContent("2"));
  });

  it("REQ-BT-055 a list at the cap says the count is a floor", async () => {
    const rows = Array.from({ length: 200 }, (_, i) => made(`c${i}`, "Closed"));
    open("olivia", rows);
    await waitFor(() => expect(stat("Closed today")).toHaveTextContent("At least 200"));
  });

  it("REQ-BT-055 nothing waiting says so", async () => {
    open("olivia");
    expect(await screen.findByText("Nothing is waiting for you")).toBeInTheDocument();
  });

  it("REQ-BT-055 a failed read says what happened, and the map stays", async () => {
    const calls = installApi({ ...server("olivia", []), "GET /api/batches": { status: 500, body: { title: "Boom", correlationId: "corr-5" } } });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    renderApp("/dashboard");
    expect((await screen.findAllByText("The server could not finish this")).length).toBeGreaterThan(0);
    expect(screen.getByRole("img", { name: /Created/ })).toBeInTheDocument();
    expect(calls.filter((c) => c.url.includes("/api/batches")).length).toBeGreaterThan(0);
  });

  it("REQ-BT-055 a batch that moves on leaves the list live", async () => {
    open("olivia", [made("ready", "Dose1")]);
    const table = await screen.findByRole("table", { name: "Waiting for your next step" });
    expect(within(table).getByText("Product ready")).toBeInTheDocument();
    act(() => FakeSocket.find("/ws/hops").send({ type: "batch", etag: '"v2"', batch: made("ready", "Dose2", { version: 2 }) }));
    await screen.findByText("Nothing is waiting for you");
  });

  it("REQ-BT-055 a lost feed says the totals are old", async () => {
    open("olivia", [], { byState: { Created: 3 } });
    await screen.findByRole("img", { name: /3 Created/ });
    await act(async () => FakeSocket.find("/ws/hops").drop());
    expect(await screen.findByText(/Totals are from/)).toBeInTheDocument();
  });
});

describe("REQ-BT-055 inspector", () => {
  it("REQ-BT-055 lists batches waiting for inspection, oldest first, with a link to act", async () => {
    open("ian", [made("new", "Dose2"), made("old", "Dose2", { createdAt: YESTERDAY }), made("other", "Inspected")], { byState: { Dose2: 2 } });
    const table = await screen.findByRole("table", { name: "Waiting for inspection" });
    const links = within(table).getAllByRole("link", { name: /^Inspect / });
    expect(links.map((l) => l.textContent)).toEqual(["Inspect Product old", "Inspect Product new"]);
    expect(links[0]).toHaveAttribute("href", "/batches/farm-a/old");
    expect(stat("Waiting for inspection")).toHaveTextContent("2");
  });

  it("REQ-BT-055 passed counts batches that moved on past inspection", async () => {
    const passed = (id: string, state: BatchState) => made(id, state, { steps: [at("Inspected"), at(state)] });
    open("ian", [passed("a", "Inspected"), passed("b", "Shipped"), passed("c", "Closed"), made("d", "Inspected", { steps: [at("Inspected", YESTERDAY)] })]);
    await waitFor(() => expect(stat("Passed today")).toHaveTextContent("3"));
  });

  it("REQ-BT-055 failed counts rejections today", async () => {
    open("ian", [made("a", "Rejected"), made("b", "Rejected", { steps: [at("Rejected", YESTERDAY)] })]);
    await waitFor(() => expect(stat("Failed today")).toHaveTextContent("1"));
  });

  it("REQ-BT-055 more waiting than listed says what is missing", async () => {
    open("ian", [made("a", "Dose2")], { byState: { Dose2: 900 } });
    expect(await screen.findByText(/Showing the newest 1 of 900/)).toBeInTheDocument();
  });

  it("REQ-BT-055 nothing to inspect says so", async () => {
    open("ian");
    expect(await screen.findByText("Nothing is waiting for inspection")).toBeInTheDocument();
  });
});

describe("REQ-BT-055 read only", () => {
  it("REQ-BT-055 shows recent closed, newest closed first, on their farm", async () => {
    open("viewer", [made("old", "Closed", { steps: [at("Closed", YESTERDAY)] }), made("new", "Closed"), made("far", "Closed", { farmId: "farm-b" })]);
    const table = await screen.findByRole("table", { name: "Recently closed" });
    expect(within(table).getAllByRole("link").map((l) => l.textContent)).toEqual(["Product new", "Product old"]);
  });

  it("REQ-BT-055 has no action control", async () => {
    open("viewer", [made("a", "Closed")]);
    await screen.findByRole("table", { name: "Recently closed" });
    const main = screen.getByRole("main");
    expect(within(main).queryAllByRole("button")).toEqual([]);
    expect(within(main).queryByRole("link", { name: /^Inspect |^Move / })).toBeNull();
  });
});

describe("REQ-BT-055 logs stay with admins", () => {
  it.each<Role>(["olivia", "ian", "viewer"])("REQ-BT-055 %s has no Logs link and a direct visit lands on their dashboard", async (user) => {
    open(user, [], {}, "/logs");
    expect(await screen.findByRole("heading", { name: "Batches by step" })).toBeInTheDocument();
    expect(within(nav()).queryByRole("link", { name: "Logs" })).toBeNull();
    expect(within(nav()).getByRole("link", { name: "Dashboard" })).toBeInTheDocument();
  });

  it("REQ-BT-055 a direct visit reads nothing from the log service", async () => {
    const calls = open("olivia", [], {}, "/logs");
    await screen.findByRole("heading", { name: "Batches by step" });
    expect(calls.filter((c) => c.url.includes("/line/"))).toEqual([]);
  });

  it("REQ-BT-055 an admin keeps Logs", async () => {
    open("ada", [], {}, "/logs");
    expect(await screen.findByRole("heading", { level: 1, name: "Logs" })).toBeInTheDocument();
    expect(within(nav()).getByRole("link", { name: "Logs" })).toBeInTheDocument();
  });
});

describe("REQ-BT-055 wording", () => {
  it("REQ-BT-055 sign in describes what the admin gets", async () => {
    installApi({});
    FakeSocket.greet(MONITOR_WS, { type: "operations", operations: [] });
    renderApp("/signin");
    expect(await screen.findByText(/Everything, on all farms, plus the cluster view, logs and replay\./)).toBeInTheDocument();
  });
});
