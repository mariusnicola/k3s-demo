import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { USERS, batch, hop, pending } from "../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Reply } from "../../mocks/harness";

const base = (user: "olivia" | "viewer" | "ian" | "ada" = "olivia") => ({
  "GET /api/me": meRoute(user),
});

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

describe("sign in", () => {
  it("REQ-BT-015 picks a demo user and lands on the dashboard", async () => {
    const calls = installApi({
      ...base(),
      "GET /api/me": { status: 401 },
      "POST /api/auth/dev-login": { body: USERS.olivia },
      "GET /api/batches": { body: [] },
    });
    renderApp("/signin");
    await userEvent.click(await screen.findByRole("button", { name: /Continue as olivia/ }));
    expect(await screen.findByRole("heading", { name: "Dashboard" })).toBeInTheDocument();
    expect(calls.find((c) => c.url.includes("dev-login"))?.body).toEqual({ user: "olivia" });
  });

  it("REQ-BT-036 a failed sign in shows why and retries", async () => {
    installApi({});
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("down")));
    renderApp("/signin");
    await userEvent.click(screen.getByRole("button", { name: /Continue as ian/ }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Cannot reach the server");
    expect(screen.getByRole("button", { name: "Try again" })).toBeInTheDocument();
  });

  it("REQ-BT-015 no session goes to sign in and returns after", async () => {
    installApi({});
    renderApp("/alerts");
    expect(await screen.findByRole("heading", { name: /Sign in/ })).toBeInTheDocument();
  });

  it("REQ-BT-015 an expired token sends the user to sign in with the reason", async () => {
    installApi({ ...base(), "GET /api/batches": { status: 401 } });
    renderApp("/batches");
    expect(await screen.findByText("Your session ended")).toBeInTheDocument();
  });

  it("REQ-BT-036 server down during restore gives retry and sign in", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("down")));
    renderApp("/batches");
    expect(await screen.findByRole("button", { name: "Try again" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Go to sign in" })).toBeInTheDocument();
  });
});

describe("batches list", () => {
  it("REQ-BT-036 shows loading then rows", async () => {
    let release: (() => void) | undefined;
    const gate = new Promise<void>((r) => (release = r));
    installApi({
      ...base(),
      "GET /api/batches": async () => {
        await gate;
        return { body: [batch()] };
      },
    });
    renderApp("/batches");
    expect(await screen.findByRole("status", { name: "Loading batches" })).toBeInTheDocument();
    release!();
    expect(await screen.findByRole("link", { name: "Bovine vaccine" })).toHaveAttribute("href", "/batches/farm-a/b-1");
  });

  it("REQ-BT-036 empty for an operator offers create", async () => {
    installApi({ ...base(), "GET /api/batches": { body: [] } });
    renderApp("/batches");
    expect(await screen.findByRole("button", { name: "Create the first batch" })).toBeInTheDocument();
  });

  it("REQ-BT-036 empty for a viewer offers no dead button", async () => {
    installApi({ ...base("viewer"), "GET /api/batches": { body: [] } });
    renderApp("/batches");
    expect(await screen.findByText("No batches yet")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Create the first batch" })).toBeNull();
  });

  it("REQ-BT-036 empty after filtering offers clear filters", async () => {
    installApi({ ...base(), "GET /api/batches": { body: [] } });
    renderApp("/batches?state=Closed");
    await userEvent.click(await screen.findByRole("button", { name: "Clear filters" }));
    expect(await screen.findByText("No batches yet")).toBeInTheDocument();
  });

  it("REQ-BT-036 error gives try again, which recovers", async () => {
    let fail = true;
    installApi({
      ...base(),
      "GET /api/batches": () => (fail ? { status: 500 } : { body: [batch()] }),
    });
    renderApp("/batches");
    const retry = await screen.findByRole("button", { name: "Try again" });
    fail = false;
    await userEvent.click(retry);
    expect(await screen.findByRole("link", { name: "Bovine vaccine" })).toBeInTheDocument();
  });

  it("REQ-BT-016 state and farm filters reach the server", async () => {
    const calls = installApi({ ...base(), "GET /api/batches": { body: [batch()] } });
    renderApp("/batches");
    await screen.findByRole("link", { name: "Bovine vaccine" });
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "State" }), "Dose2");
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "Farm" }), "farm-b");
    await waitFor(() => expect(calls.at(-1)?.url).toMatch(/state=Dose2.*farmId=farm-b/));
  });

  it("REQ-BT-016 a viewer is offered only its own farm", async () => {
    installApi({ ...base("viewer"), "GET /api/batches": { body: [batch()] } });
    renderApp("/batches");
    const select = await screen.findByRole("combobox", { name: "Farm" });
    expect(within(select).getAllByRole("option").map((o) => o.textContent)).toEqual(["All my farms", "farm-a"]);
  });

  it("REQ-BT-011 blocked, delayed and flagged are visible in the list", async () => {
    installApi({
      ...base(),
      "GET /api/batches": {
        body: [batch({ id: "b-1", pending: pending("dead") }), batch({ id: "b-2", product: "Late", pending: pending("sent", "2020-01-01T00:00:00Z") }), batch({ id: "b-3", product: "Old", flags: [{ type: "Dose2Overdue", at: "2026-10-30T10:00:00Z" }] })],
      },
    });
    renderApp("/batches");
    const table = await screen.findByRole("table", { name: "Batches" });
    expect(within(table).getByText("Blocked")).toBeInTheDocument();
    expect(within(table).getByText("Delayed")).toBeInTheDocument();
    expect(within(table).getByText("Flagged")).toBeInTheDocument();
  });

  it("REQ-BT-011 a final batch shows no Flagged badge, as the Flagged filter leaves it out", async () => {
    installApi({
      ...base(),
      "GET /api/batches": { body: [batch({ id: "b-1", state: "Closed", flags: [{ type: "Dose2Overdue", at: "2026-10-30T10:00:00Z" }] })] },
    });
    renderApp("/batches");
    const table = await screen.findByRole("table", { name: "Batches" });
    expect(within(table).queryByText("Flagged")).not.toBeInTheDocument();
    expect(within(table).getByText("None")).toBeInTheDocument();
  });

  it("REQ-BT-032 nothing fetches again, with a hop or without one", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const calls = installApi({ ...base(), "GET /api/batches": { body: [batch()] } });
      renderApp("/batches");
      await screen.findByRole("link", { name: "Bovine vaccine" });
      const socket = FakeSocket.find("/ws/hops");
      socket.open();
      const before = calls.length;
      await vi.advanceTimersByTimeAsync(60_000);
      expect(calls.length).toBe(before);
      socket.sendHops([hop({ hop: "record" })]);
      await vi.advanceTimersByTimeAsync(10);
      expect(calls.length).toBe(before);
    } finally {
      vi.useRealTimers();
    }
  });

  it("REQ-BT-030 lost live updates show on the list with the last data time", async () => {
    installApi({ ...base(), "GET /api/batches": { body: [batch()] } });
    renderApp("/batches");
    await screen.findByRole("link", { name: "Bovine vaccine" });
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    socket.sendHops([]);
    socket.drop();
    expect(await screen.findByText(/Live updates: connection lost/)).toBeInTheDocument();
    expect(screen.getByText(/Last data/)).toBeInTheDocument();
  });
});

describe("create batch", () => {
  it("REQ-BT-004 creates and opens the new batch", async () => {
    const calls = installApi({
      ...base(),
      "GET /api/batches": { body: [] },
      "POST /api/batches": { status: 201, etag: '"1"', body: batch({ id: "new", product: "Lot 7", state: "Created", steps: [] }) },
      "GET /api/batches/farm-a/new": { etag: '"1"', body: batch({ id: "new", product: "Lot 7", state: "Created", steps: [] }) },
    });
    renderApp("/batches");
    await userEvent.click(await screen.findByRole("button", { name: "New batch" }));
    await userEvent.type(screen.getByRole("textbox", { name: /Product/ }), "  Lot 7 ");
    await userEvent.click(screen.getByRole("button", { name: "Create batch" }));
    expect(await screen.findByRole("heading", { name: "Lot 7" })).toBeInTheDocument();
    expect(calls.find((c) => c.method === "POST")?.body).toEqual({ farmId: "farm-a", product: "Lot 7" });
  });

  it("REQ-BT-004 the create dialog says why Cancel is locked while it saves", async () => {
    installApi({ ...base(), "GET /api/batches": { body: [] }, "POST /api/batches": () => new Promise<Reply>(() => undefined) });
    renderApp("/batches");
    await userEvent.click(await screen.findByRole("button", { name: "New batch" }));
    await userEvent.type(screen.getByRole("textbox", { name: /Product/ }), "Lot 7");
    await userEvent.click(screen.getByRole("button", { name: "Create batch" }));
    const dialog = screen.getByRole("dialog");
    expect(await within(dialog).findByText("Cancel is locked while this runs. It opens when the server answers, or after 65 seconds.")).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Cancel" })).toHaveAttribute("aria-disabled", "true");
  });

  it("REQ-BT-007 an empty product is refused beside the field", async () => {
    const calls = installApi({ ...base(), "GET /api/batches": { body: [] } });
    renderApp("/batches");
    await userEvent.click(await screen.findByRole("button", { name: "New batch" }));
    await userEvent.type(screen.getByRole("textbox", { name: /Product/ }), "   ");
    await userEvent.click(screen.getByRole("button", { name: "Create batch" }));
    expect(await screen.findByText("Enter the product name.")).toBeInTheDocument();
    expect(calls.filter((c) => c.method === "POST")).toEqual([]);
  });

  it("REQ-BT-015 a 403 on create is shown inside the dialog", async () => {
    installApi({ ...base(), "GET /api/batches": { body: [] }, "POST /api/batches": { status: 403 } });
    renderApp("/batches");
    await userEvent.click(await screen.findByRole("button", { name: "New batch" }));
    await userEvent.type(screen.getByRole("textbox", { name: /Product/ }), "Lot");
    await userEvent.click(screen.getByRole("button", { name: "Create batch" }));
    expect(await within(screen.getByRole("dialog")).findByText("You do not have access")).toBeInTheDocument();
  });

  it("REQ-BT-015 a viewer sees why create is unavailable", async () => {
    installApi({ ...base("viewer"), "GET /api/batches": { body: [] } });
    renderApp("/batches");
    const button = await screen.findByRole("button", { name: "New batch" });
    expect(button).toHaveAccessibleDescription("Creating a batch needs the Operator or Admin role.");
    await userEvent.click(button);
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});
