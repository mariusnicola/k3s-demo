import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ClusterOperation } from "../../api/monitorTypes";
import { MONITOR_WS } from "../../config";
import { REPORT, USERS } from "../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../../mocks/harness";

const NOW = new Date(2026, 9, 31, 12, 0, 0).getTime();
const TODAY = new Date(NOW - 3_600_000).toISOString();

function operation(over: Partial<ClusterOperation> = {}): ClusterOperation {
  const at = new Date(NOW - 90_000).toISOString();
  return { id: "one", name: "Deploy the app", step: "Charts", startedAt: at, lastSeenAt: at, state: "running", endedAt: null, steps: [], etaSeconds: null, ...over };
}

async function send(operations: ClusterOperation[]) {
  await act(async () => {
    FakeSocket.exact(MONITOR_WS).open();
    FakeSocket.exact(MONITOR_WS).send({ type: "operations", operations });
  });
}


beforeEach(() => {
  useFakeSockets();
  localStorage.clear();
  vi.spyOn(Date, "now").mockReturnValue(NOW);
});

afterEach(() => void vi.restoreAllMocks());

describe("REQ-BT-054 operations are honest and quiet", () => {
  beforeEach(() => void installApi({ "GET /api/me": meRoute("olivia"), "GET /me": { body: USERS.olivia }, "GET /api/batches": { body: [] } }));

  it("REQ-BT-054 the running clock and estimate are not read out", async () => {
    renderApp("/signin");
    await send([operation({ steps: [{ name: "Charts", state: "running", startedAt: TODAY, endedAt: null }], etaSeconds: 900 })]);
    expect(document.querySelector(".ds-operation-elapsed")).toHaveAttribute("aria-live", "off");
    expect(screen.getByText(/min left/)).toHaveAttribute("aria-live", "off");
  });

  it("REQ-BT-054 a stepless row names its state in words", async () => {
    renderApp("/signin");
    await send([operation()]);
    expect(screen.getByText("Deploy the app").closest(".ds-operation")).toHaveTextContent("Running");
  });

  it("REQ-BT-054 a lost socket stops a one line operation too", async () => {
    renderApp("/signin");
    await send([operation()]);
    await act(async () => FakeSocket.exact(MONITOR_WS).drop());
    await screen.findByText("Cluster work state unknown");
    const row = screen.getByText("Deploy the app").closest(".ds-operation")!;
    expect(row.querySelector(".ds-spinner")).toBeNull();
    expect(row.querySelector(".ds-operation-elapsed")).toBeNull();
    expect(row).toHaveTextContent("Last known step: Charts");
  });

  it("REQ-BT-054 no signal says when it was last heard, what to do, and can be dismissed", async () => {
    renderApp("/signin");
    await send([operation({ state: "no-signal", lastSeenAt: TODAY })]);
    const row = screen.getByText("No signal from Deploy the app").closest(".ds-operation")!;
    expect(row).toHaveTextContent("Last heard");
    await waitFor(() => expect(row).toHaveTextContent("Ask an admin to check it."));
    await userEvent.click(within(row as HTMLElement).getByRole("button", { name: /^Seen/ }));
    expect(screen.queryByText("No signal from Deploy the app")).toBeNull();
  });

  it("REQ-BT-054 seen on one run never hides a later run of the same work", async () => {
    renderApp("/signin");
    await send([operation({ id: "rollout:api", state: "no-signal", startedAt: TODAY })]);
    await userEvent.click(await screen.findByRole("button", { name: /^Seen/ }));
    await send([operation({ id: "rollout:api", state: "no-signal", startedAt: new Date(NOW - 1000).toISOString() })]);
    expect(screen.getByText("No signal from Deploy the app")).toBeInTheDocument();
  });

  it("REQ-BT-054 a second silence of the same run shows again", async () => {
    renderApp("/signin");
    await send([operation({ state: "no-signal", lastSeenAt: TODAY })]);
    await userEvent.click(await screen.findByRole("button", { name: /^Seen/ }));
    await send([operation({ state: "running", lastSeenAt: new Date(NOW).toISOString() })]);
    await send([operation({ state: "no-signal", lastSeenAt: new Date(NOW).toISOString() })]);
    expect(screen.getByText("No signal from Deploy the app")).toBeInTheDocument();
  });

  it("REQ-BT-054 one user pressing seen does not hide it from the next", async () => {
    const view = renderApp("/signin");
    await send([operation({ state: "failed", endedAt: TODAY })]);
    await userEvent.click(await screen.findByRole("button", { name: /^Seen/ }));
    view.unmount();
    installApi({ "GET /api/me": meRoute("ada") });
    renderApp("/signin");
    await send([operation({ state: "failed", endedAt: TODAY })]);
    expect(await screen.findByText("Deploy the app failed")).toBeInTheDocument();
  });

  it("REQ-BT-054 nobody signed out can dismiss a failure", async () => {
    installApi({ "GET /api/me": { status: 401 } });
    renderApp("/signin");
    await send([operation({ state: "failed", endedAt: TODAY })]);
    await screen.findByText("Deploy the app failed");
    expect(screen.queryByRole("button", { name: /^Seen/ })).toBeNull();
  });

  it("REQ-BT-054 a failure says when it happened", async () => {
    renderApp("/signin");
    await send([operation({ state: "failed", endedAt: TODAY })]);
    expect(screen.getByText("Deploy the app failed").closest(".ds-operation")).toHaveTextContent("Ended Oct 31, 2026");
  });

  it("REQ-BT-054 a rerun that passed retires the failed run", async () => {
    const steps = [{ name: "Charts", state: "done" as const, startedAt: TODAY, endedAt: TODAY }];
    renderApp("/signin");
    await send([operation({ work: "deploy", state: "failed", endedAt: TODAY, steps })]);
    await send([operation({ id: "two", name: "Deploy again", work: "deploy", state: "done", endedAt: TODAY, steps })]);
    await screen.findByText("Deploy again finished");
    expect(screen.queryByText("Deploy the app failed")).toBeNull();
  });

  it("REQ-BT-054 a lost socket does not say running", async () => {
    renderApp("/signin");
    await send([operation(), operation({ id: "two", name: "Roll web" })]);
    await act(async () => FakeSocket.exact(MONITOR_WS).drop());
    expect(await screen.findByText("Last known: 2 operations running")).toBeInTheDocument();
  });

  it("REQ-BT-054 an open socket gone silent counts as no signal", async () => {
    installApi({ "GET /api/me": meRoute("ada"), "GET /api/batches": { body: [] } });
    renderApp("/signin");
    await send([operation()]);
    await act(async () => FakeSocket.exact(MONITOR_WS).send({ ...REPORT, checkedAt: new Date(NOW).toISOString() }));
    vi.spyOn(Date, "now").mockReturnValue(NOW + 3_600_000);
    await act(async () => FakeSocket.exact(MONITOR_WS).send({ type: "operations", operations: [operation()] }));
    await waitFor(() => expect(screen.getByText("Deploy the app").closest(".ds-operation")!.querySelector(".ds-spinner")).toBeNull(), { timeout: 3000 });
  });

  it("REQ-BT-054 seen is remembered across a reload", async () => {
    const view = renderApp("/signin");
    await send([operation({ state: "failed", endedAt: TODAY })]);
    await userEvent.click(screen.getByRole("button", { name: /^Seen/ }));
    view.unmount();
    renderApp("/signin");
    await send([operation({ state: "failed", endedAt: TODAY })]);
    expect(screen.queryByText("Deploy the app failed")).toBeNull();
  });

  it("REQ-BT-053 a failed operation sends a non admin to an admin", async () => {
    renderApp("/batches");
    await send([operation({ state: "failed", endedAt: TODAY })]);
    const row = (await screen.findByText("Deploy the app failed")).closest(".ds-operation")!;
    await waitFor(() => expect(row).toHaveTextContent("Ask an admin to check it."));
    expect(row).not.toHaveTextContent("run it again");
  });
});
