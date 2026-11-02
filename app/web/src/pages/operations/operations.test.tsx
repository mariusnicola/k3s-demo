import { act, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { MONITOR_WS } from "../../config";
import type { ClusterOperation } from "../../api/monitorTypes";
import { FakeSocket, installApi, renderApp, useFakeSockets } from "../../mocks/harness";
import { USERS } from "../../mocks/fixtures";

const AT = new Date(Date.now() - 90_000).toISOString();

function operation(over: Partial<ClusterOperation>): ClusterOperation {
  return { id: "one", name: "Deploy the app", step: null, startedAt: AT, lastSeenAt: AT, state: "running", endedAt: null, ...over };
}

async function send(operations: ClusterOperation[]) {
  await act(async () => {
    FakeSocket.exact(MONITOR_WS).open();
    FakeSocket.exact(MONITOR_WS).send({ type: "operations", operations });
  });
}

describe("REQ-BT-043 cluster work banner", () => {
  beforeEach(() => {
    useFakeSockets();
    installApi({ "GET /me": { body: USERS.olivia }, "GET /batches": { body: { items: [] } } });
  });

  it.each(["/signin", "/design-system", "/"])("shows the running operation, its step and elapsed time on %s", async (path) => {
    // Frozen, or the elapsed text moves with however long this machine takes to get here.
    vi.spyOn(Date, "now").mockReturnValue(Date.parse(AT) + 90_000);
    renderApp(path);
    await send([operation({ step: "Charts" })]);
    const banner = await screen.findByText("Deploy the app");
    expect(banner.closest(".ds-operation")).toHaveTextContent("Charts");
    expect(banner.closest(".ds-operation")).toHaveTextContent("1 min 30 s");
    expect(banner.closest(".ds-operation")?.querySelector(".ds-spinner")).not.toBeNull();
    expect(within(banner.closest(".ds-operation") as HTMLElement).queryByRole("button")).toBeNull();
    vi.restoreAllMocks();
  });

  it("clears when the monitor drops the finished operation", async () => {
    renderApp("/signin");
    await send([operation({ state: "done", endedAt: AT })]);
    expect(await screen.findByText("Deploy the app finished")).toBeInTheDocument();
    await send([]);
    expect(screen.queryByText("Deploy the app finished")).not.toBeInTheDocument();
  });

  it("keeps a failure until someone says they saw it", async () => {
    installApi({ "GET /api/me": { body: USERS.olivia }, "GET /api/batches": { body: [] } });
    renderApp("/signin");
    await send([operation({ state: "failed", endedAt: AT, step: "Charts" })]);
    expect(await screen.findByText("Deploy the app failed")).toBeInTheDocument();
    await send([operation({ state: "failed", endedAt: AT, step: "Charts" })]);
    expect(screen.getByText("Deploy the app failed")).toBeInTheDocument();
    await userEvent.click(await screen.findByRole("button", { name: /^Seen/ }));
    expect(screen.queryByText("Deploy the app failed")).not.toBeInTheDocument();
  });

  it("says no signal instead of spinning forever", async () => {
    renderApp("/signin");
    await send([operation({ state: "no-signal" })]);
    const banner = await screen.findByText("No signal from Deploy the app");
    expect(banner.closest(".ds-operation")).toHaveTextContent("State unknown.");
    expect(banner.closest(".ds-operation")?.querySelector(".ds-spinner")).toBeNull();
  });

  it("says state unknown when the socket is lost, never no operation", async () => {
    renderApp("/signin");
    await send([operation({})]);
    await act(async () => FakeSocket.exact(MONITOR_WS).drop());
    expect(await screen.findByText("Cluster work state unknown")).toBeInTheDocument();
  });

  it("joins simultaneous work into one row", async () => {
    renderApp("/signin");
    await send([operation({ id: "a", name: "Rollout api" }), operation({ id: "b", name: "Rollout web" })]);
    const banner = await screen.findByText("2 operations running");
    expect(banner.closest(".ds-operation")).toHaveTextContent("Rollout api, Rollout web");
  });

  it("shows nothing when the monitor says nothing is running", async () => {
    renderApp("/signin");
    await send([]);
    expect(document.querySelectorAll(".ds-operation")).toHaveLength(0);
  });
});
