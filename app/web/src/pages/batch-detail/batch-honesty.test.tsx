import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import type { Batch } from "../../api/types";
import { batch, pending, step } from "../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Handler, type Reply } from "../../mocks/harness";

const ROUTE = "GET /api/batches/farm-a/b-1";

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

describe("batch page tells the truth about its record", () => {
  function page(user: "olivia" | "ian" | "ada", value: Batch | (() => Reply), extra: Record<string, Reply | Handler> = {}) {
    const calls = installApi({ "GET /api/me": meRoute(user), [ROUTE]: typeof value === "function" ? value : { etag: '"v1"', body: value }, ...extra });
    renderApp("/batches/farm-a/b-1");
    return calls;
  }

  it("REQ-BT-036 Closed with an unwritten record is not shown as complete", async () => {
    page("ada", batch({ state: "Closed", pending: pending("dead", null, "refused") }));
    const banner = (await screen.findByText("Closed, final")).closest(".ds-banner")!;
    expect(banner).toHaveAttribute("data-tone", "warning");
    expect(banner).toHaveTextContent("not written yet");
    expect(banner).not.toHaveTextContent("complete");
  });

  it("REQ-BT-036 an in-flight event on a final batch promises no next step", async () => {
    page("olivia", batch({ state: "Closed", pending: pending("sent", new Date().toISOString()) }));
    const banner = (await screen.findByText("Compliance record is delayed")).closest(".ds-banner")!;
    expect(banner).toHaveTextContent("only its record is outstanding");
    expect(banner).not.toHaveTextContent("next step");
  });

  it("REQ-BT-002 Closed with every record written reads complete", async () => {
    page("olivia", batch({ state: "Closed" }));
    const banner = (await screen.findByText("Closed, final")).closest(".ds-banner")!;
    expect(banner).toHaveAttribute("data-tone", "success");
    expect(banner).toHaveTextContent("This batch is complete");
  });

  it("REQ-BT-021 a resend does not restart the delayed clock", async () => {
    page("olivia", batch({ steps: [step("Dose1", false, { at: "2020-01-01T00:00:00Z" })], pending: pending("sent", new Date().toISOString()) }));
    expect(await screen.findByText("Compliance record is delayed")).toBeInTheDocument();
  });

  it("REQ-BT-035 a matching chain link never reads as a verified success", async () => {
    page("olivia", batch());
    await userEvent.click(await screen.findByRole("tab", { name: "Compliance records" }));
    const badge = await screen.findByText("Links to previous");
    expect(badge.closest(".ds-badge")).toHaveAttribute("data-tone", "neutral");
  });

  it("REQ-BT-036 a success notice leaves when someone else moves the batch", async () => {
    let current = batch();
    page("olivia", () => ({ etag: '"v1"', body: current }), {
      "POST /api/batches/farm-a/b-1/advance": () => {
        current = batch({ version: 2, state: "Dose2" });
        return { etag: '"v2"', body: current };
      },
    });
    await userEvent.click(await screen.findByRole("button", { name: "Move to Dose 2" }));
    expect(await screen.findByText("Moved to Dose 2.")).toBeInTheDocument();
    const socket = FakeSocket.find("/ws/hops");
    socket.open();
    act(() => socket.send({ type: "batch", etag: '"v3"', batch: batch({ version: 3, state: "Inspected" }) }));
    await waitFor(() => expect(screen.queryByText("Moved to Dose 2.")).toBeNull(), { timeout: 3000 });
  });

  it("REQ-BT-002 Cancel clears a half written rejection", async () => {
    page("ian", batch({ state: "Dose2" }));
    await userEvent.click(await screen.findByRole("button", { name: "Record inspection" }));
    await userEvent.click(screen.getByRole("radio", { name: /Fail/ }));
    await userEvent.click(screen.getByRole("textbox", { name: /Reason/ }));
    await userEvent.paste("Cold chain");
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await userEvent.click(screen.getByRole("button", { name: "Record inspection" }));
    expect(screen.getByRole("button", { name: "Record pass" })).toBeInTheDocument();
    expect(screen.queryByRole("textbox", { name: /Reason/ })).toBeNull();
  });

  it("REQ-BT-011 fail next on a batch closed elsewhere is refused with the reason", async () => {
    let current = batch();
    const calls = page("ada", () => ({ etag: '"v1"', body: current }));
    const open = await screen.findByRole("button", { name: "Fail next delivery" });
    current = batch({ state: "Closed" });
    await userEvent.click(open);
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Fail next delivery" }));
    expect((await screen.findAllByText("This batch is final and has no next delivery.")).length).toBeGreaterThan(0);
    expect(screen.getByRole("alert")).toHaveTextContent("This step is not allowed right now");
    expect(calls.some((c) => c.url.endsWith("/fail-next"))).toBe(false);
  });

  it("REQ-BT-011 a blocked batch does not promise a failure that Replay will clear", async () => {
    page("ada", batch({ failNext: true, pending: pending("dead", null, "refused") }));
    expect(await screen.findByRole("button", { name: "Fail next delivery" })).toHaveAccessibleDescription(/Replay clears any failure/);
    expect(screen.queryByText("Next delivery set to fail")).toBeNull();
  });

  it("REQ-BT-036 a dialog cannot be left while its write runs", async () => {
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => (release = resolve));
    page("olivia", batch({ state: "Shipped" }), {
      "POST /api/batches/farm-a/b-1/advance": async () => {
        await gate;
        return { etag: '"v2"', body: batch({ version: 2, state: "Closed" }) };
      },
    });
    await userEvent.click(await screen.findByRole("button", { name: "Move to Closed" }));
    const dialog = screen.getByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Close batch" }));
    await userEvent.keyboard("{Escape}");
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Cancel" })).toHaveAttribute("aria-disabled", "true");
    await act(async () => release());
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});
