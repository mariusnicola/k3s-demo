import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import type { Batch } from "../../api/types";
import { batch, pending, step } from "../../mocks/fixtures";
import { installApi, meRoute, renderApp, useFakeSockets, type Handler, type Reply } from "../../mocks/harness";

const PATH = "/batches/farm-a/b-1";
const ROUTE = "GET /api/batches/farm-a/b-1";

function page(user: "olivia" | "ian" | "ada" | "viewer", value: Batch | (() => Reply), extra: Record<string, Reply | Handler> = {}) {
  const calls = installApi({
    "GET /api/me": meRoute(user),
    [ROUTE]: typeof value === "function" ? value : { etag: '"v1"', body: value },
    ...extra,
  });
  renderApp(PATH);
  return calls;
}

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

describe("batch page", () => {
  it("REQ-BT-036 shows loading then the batch", async () => {
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => (release = resolve));
    installApi({
      "GET /api/me": meRoute("olivia"),
      [ROUTE]: async () => {
        await gate;
        return { etag: '"v1"', body: batch() };
      },
    });
    renderApp(PATH);
    expect(await screen.findByRole("status", { name: "Loading batch" })).toBeInTheDocument();
    release();
    expect(await screen.findByRole("heading", { name: "Bovine vaccine" })).toBeInTheDocument();
  });

  it("REQ-BT-001 advance sends the next state with If-Match, and shows the reply", async () => {
    const calls = page("olivia", batch(), { "POST /api/batches/farm-a/b-1/advance": { etag: '"v2"', body: batch({ version: 2, state: "Dose2" }) } });
    await userEvent.click(await screen.findByRole("button", { name: "Move to Dose 2" }));
    expect(await screen.findByText("Moved to Dose 2.")).toBeInTheDocument();
    const post = calls.find((c) => c.method === "POST")!;
    expect(post.body).toEqual({ to: "Dose2" });
    expect(post.headers["If-Match"]).toBe('"v1"');
    expect(await screen.findByRole("button", { name: "Record inspection" })).toBeInTheDocument();
  });

  it("REQ-BT-006 412 says someone else changed it, and Reload batch recovers", async () => {
    let version = 1;
    page(
      "olivia",
      () => ({ etag: `"v${version}"`, body: batch({ version, state: version === 1 ? "Dose1" : "Dose2" }) }),
      {
        "POST /api/batches/farm-a/b-1/advance": () => {
          version = 2;
          return { status: 412 };
        },
      },
    );
    await userEvent.click(await screen.findByRole("button", { name: "Move to Dose 2" }));
    expect(await screen.findByText("Someone else changed this batch")).toBeInTheDocument();
    expect(screen.getByText(/Nothing was saved/)).toBeInTheDocument();
    await userEvent.click(screen.getAllByRole("button", { name: "Reload batch" })[0]!);
    await waitFor(() => expect(screen.queryByText("Someone else changed this batch")).toBeNull());
    expect(await screen.findByText("Record inspection")).toBeInTheDocument();
  });

  it("REQ-BT-007 409 explains and offers reload", async () => {
    page("olivia", batch(), { "POST /api/batches/farm-a/b-1/advance": { status: 409, body: { title: "Conflict", detail: "An event is pending for this batch." } } });
    await userEvent.click(await screen.findByRole("button", { name: "Move to Dose 2" }));
    expect(await screen.findByText("This step is not allowed right now")).toBeInTheDocument();
    expect(screen.getByText("An event is pending for this batch.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reload batch" })).toBeInTheDocument();
  });

  it("REQ-BT-015 403 on an action gives a way out", async () => {
    page("olivia", batch(), { "POST /api/batches/farm-a/b-1/advance": { status: 403 } });
    await userEvent.click(await screen.findByRole("button", { name: "Move to Dose 2" }));
    expect(await screen.findByText("You do not have access")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Back to batches" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Switch user" })).toBeNull();
  });

  it("REQ-BT-007 404 on load gives a way out", async () => {
    page("olivia", () => ({ status: 404 }));
    expect(await screen.findByText("Not found")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Back to batches" })).toBeInTheDocument();
  });

  it("REQ-BT-016 403 on load, another farm, gives a way out", async () => {
    page("viewer", () => ({ status: 403 }));
    expect(await screen.findByText("You do not have access")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Back to batches" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Switch user" })).toBeNull();
  });

  it("REQ-BT-036 a failed read after a refused write keeps the data and says so", async () => {
    let fail = false;
    page("olivia", () => (fail ? { status: 500 } : { etag: '"v1"', body: batch() }), {
      "POST /api/batches/farm-a/b-1/advance": () => {
        fail = true;
        return { status: 412 };
      },
    });
    await userEvent.click(await screen.findByRole("button", { name: "Move to Dose 2" }));
    expect(await screen.findByText("The request failed")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Bovine vaccine" })).toBeInTheDocument();
  });

  it("REQ-BT-015 a viewer sees the action, disabled, with the reason", async () => {
    page("viewer", batch());
    const button = await screen.findByRole("button", { name: "Move to Dose 2" });
    expect(button).toHaveAttribute("aria-disabled", "true");
    expect(button).toHaveAccessibleDescription("Moving a batch needs the Operator or Admin role.");
  });

  it("REQ-BT-015 an operator at Dose 2 is told an inspector must act", async () => {
    page("olivia", batch({ state: "Dose2" }));
    expect(await screen.findByRole("button", { name: "Record inspection" })).toHaveAccessibleDescription("Inspection needs the Inspector or Admin role.");
  });

  it("REQ-BT-008 inspect is disabled while an event is in flight", async () => {
    page("ian", batch({ state: "Dose2", pending: pending("sent", new Date().toISOString()) }));
    const button = await screen.findByRole("button", { name: "Record inspection" });
    expect(button).toHaveAccessibleDescription(/in flight/);
    await userEvent.click(button);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("REQ-BT-008 no control offers Inspected", async () => {
    page("olivia", batch({ state: "Dose2" }));
    await screen.findByRole("button", { name: "Record inspection" });
    expect(screen.queryByRole("button", { name: /Move to Inspected/ })).toBeNull();
  });

  it("REQ-BT-003 a failed inspection needs a reason, then rejects", async () => {
    const calls = page("ian", batch({ state: "Dose2" }), { "POST /api/batches/farm-a/b-1/inspect": { body: {} } });
    await userEvent.click(await screen.findByRole("button", { name: "Record inspection" }));
    const dialog = screen.getByRole("dialog");
    await userEvent.click(within(dialog).getByRole("radio", { name: /Fail/ }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Reject batch" }));
    expect(await within(dialog).findByText(/needs a reason/)).toBeInTheDocument();
    expect(calls.filter((c) => c.method === "POST")).toEqual([]);
    await userEvent.click(within(dialog).getByRole("textbox", { name: /Reason/ }));
    await userEvent.paste("   ");
    await userEvent.click(within(dialog).getByRole("button", { name: "Reject batch" }));
    expect(calls.filter((c) => c.method === "POST")).toEqual([]);
    await userEvent.click(within(dialog).getByRole("textbox", { name: /Reason/ }));
    await userEvent.paste("Cold chain broken");
    await userEvent.click(within(dialog).getByRole("button", { name: "Reject batch" }));
    expect(await screen.findByText("Batch rejected.")).toBeInTheDocument();
    const post = calls.find((c) => c.method === "POST")!;
    expect(post.body).toEqual({ passed: false, reason: "Cold chain broken" });
    expect(post.headers["If-Match"]).toBe('"v1"');
  });

  it("REQ-BT-003 the inspection dialog says why Cancel is locked while it saves", async () => {
    page("ian", batch({ state: "Dose2" }), { "POST /api/batches/farm-a/b-1/inspect": () => new Promise<Reply>(() => undefined) });
    await userEvent.click(await screen.findByRole("button", { name: "Record inspection" }));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).queryByText(/Cancel is locked/)).not.toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "Record pass" }));
    expect(await within(dialog).findByText("Cancel is locked while this runs. It opens when the server answers, or after 65 seconds.")).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Cancel" })).toHaveAttribute("aria-disabled", "true");
  });

  it("REQ-BT-001 a passed inspection sends passed true", async () => {
    const calls = page("ian", batch({ state: "Dose2" }), { "POST /api/batches/farm-a/b-1/inspect": { body: {} } });
    await userEvent.click(await screen.findByRole("button", { name: "Record inspection" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Record pass" }));
    await screen.findByText("Inspection passed.");
    expect(calls.find((c) => c.method === "POST")?.body).toEqual({ passed: true, reason: null });
  });

  it("REQ-BT-001 closing asks first because Closed is final", async () => {
    const calls = page("olivia", batch({ state: "Shipped" }), { "POST /api/batches/farm-a/b-1/advance": { body: {} } });
    await userEvent.click(await screen.findByRole("button", { name: "Move to Closed" }));
    expect(calls.filter((c) => c.method === "POST")).toEqual([]);
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Close batch" }));
    await screen.findByText("Moved to Closed.");
    expect(calls.find((c) => c.method === "POST")?.body).toEqual({ to: "Closed" });
  });

  it("REQ-BT-002 Closed and Rejected say final, with the reason", async () => {
    page("olivia", batch({ state: "Rejected", reason: "Cold chain broken" }));
    expect(await screen.findByText("Rejected, final")).toBeInTheDocument();
    expect(screen.getByText("Reason: Cold chain broken")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Move to/ })).toBeNull();
  });

  it("REQ-BT-011 blocked shows for an operator, who is told an admin must act", async () => {
    page("olivia", batch({ pending: pending("dead", null, "failNext") }));
    expect(await screen.findByText("Blocked: the compliance event failed")).toBeInTheDocument();
    expect(screen.getByText("failNext").closest("details")).not.toHaveAttribute("open");
    expect(screen.getByRole("button", { name: "Replay event" })).toHaveAccessibleDescription("Replay needs the Admin role.");
    expect(screen.getByRole("button", { name: "Move to Dose 2" })).toHaveAccessibleDescription(/Blocked/);
  });

  it("REQ-BT-011 an admin replays a blocked batch", async () => {
    let blocked = true;
    const calls = page("ada", () => ({ etag: '"v1"', body: batch({ pending: blocked ? pending("dead") : null }) }), {
      "POST /api/batches/farm-a/b-1/replay": () => {
        blocked = false;
        return { etag: '"v2"', body: batch({ version: 2, pending: null }) };
      },
    });
    await userEvent.click(await screen.findByRole("button", { name: "Replay event" }));
    expect(await screen.findByText(/Replay requested/)).toBeInTheDocument();
    expect(calls.some((c) => c.method === "POST" && c.url.endsWith("/replay"))).toBe(true);
    await waitFor(() => expect(screen.queryByText(/Blocked: the/)).toBeNull());
  });

  it("REQ-BT-011 replay 409, not Blocked any more, offers reload", async () => {
    page("ada", batch({ pending: pending("dead") }), { "POST /api/batches/farm-a/b-1/replay": { status: 409, body: { title: "Conflict", detail: "Batch is not Blocked." } } });
    await userEvent.click(await screen.findByRole("button", { name: "Replay event" }));
    expect(await screen.findByText("Batch is not Blocked.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reload batch" })).toBeInTheDocument();
  });

  it("REQ-BT-011 replay is not offered on a healthy batch", async () => {
    page("ada", batch());
    await screen.findByRole("heading", { name: "Bovine vaccine" });
    expect(screen.queryByRole("button", { name: "Replay event" })).toBeNull();
  });

  it("REQ-BT-011 fail next asks, then shows the armed state", async () => {
    let armed = false;
    const calls = page("ada", () => ({ etag: '"v1"', body: batch({ failNext: armed }) }), {
      "POST /api/batches/farm-a/b-1/fail-next": () => {
        armed = true;
        return { etag: '"v2"', body: batch({ version: 2, failNext: true }) };
      },
    });
    await userEvent.click(await screen.findByRole("button", { name: "Fail next delivery" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Fail next delivery" }));
    expect(await screen.findByText("Next delivery set to fail")).toBeInTheDocument();
    expect(calls.filter((c) => c.url.endsWith("/fail-next"))).toHaveLength(1);
  });

  it("REQ-BT-011 non admins do not see failure injection", async () => {
    page("olivia", batch());
    await screen.findByRole("heading", { name: "Bovine vaccine" });
    expect(screen.queryByRole("button", { name: "Fail next delivery" })).toBeNull();
  });

  it("REQ-BT-021 an old event reads delayed, with what the system is doing", async () => {
    page("olivia", batch({ pending: pending("sent", new Date().toISOString()) }));
    expect(await screen.findByText("Compliance record is delayed")).toBeInTheDocument();
    expect(screen.getByText(/holds delivery while the Worker is down/)).toBeInTheDocument();
  });

  it("REQ-BT-009 timeline and compliance records show written and missing records", async () => {
    page("olivia", batch({ steps: [step("Created"), step("Dose1", true, { previousHash: "hash-Created" }), step("Dose2", false)], state: "Dose2", pending: pending("sent", new Date().toISOString()) }));
    expect(await screen.findByText("Compliance record not written yet")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("tab", { name: "Compliance records" }));
    const table = await screen.findByRole("table", { name: "Compliance records" });
    expect(within(table).getAllByRole("row")).toHaveLength(3);
    expect(within(table).getByText("Links to previous")).toBeInTheDocument();
    expect(within(table).getByText("Start of chain")).toBeInTheDocument();
  });

  it("REQ-BT-012 a chain mismatch is visible", async () => {
    page("olivia", batch({ steps: [step("Created"), step("Dose1", true, { previousHash: "tampered" })] }));
    await userEvent.click(await screen.findByRole("tab", { name: "Compliance records" }));
    expect(await screen.findByText("Link mismatch")).toBeInTheDocument();
  });

  it("REQ-BT-036 no steps and no records are designed", async () => {
    page("olivia", batch({ state: "Created", steps: [] }));
    expect(await screen.findByText("No steps yet")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("tab", { name: "Compliance records" }));
    expect(await screen.findByText("No compliance records yet")).toBeInTheDocument();
  });

});
