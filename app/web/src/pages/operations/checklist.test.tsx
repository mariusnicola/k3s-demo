import { act, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MONITOR_WS } from "../../config";
import type { ClusterOperation, OperationStep } from "../../api/monitorTypes";
import { FakeSocket, installApi, renderApp, useFakeSockets } from "../../mocks/harness";
import { USERS } from "../../mocks/fixtures";

const NOW = Date.parse("2026-10-31T10:00:00Z");
const AT = new Date(NOW - 90_000).toISOString();
const NAME = "Deploy the app";

const step = (name: string, state: OperationStep["state"], over: Partial<OperationStep> = {}): OperationStep => ({
  name,
  state,
  startedAt: state === "pending" ? null : AT,
  endedAt: state === "done" || state === "failed" ? new Date(NOW - 30_000).toISOString() : null,
  ...over,
});
const STEPS = [step("Build images", "done"), step("Charts", "running"), step("Smoke test", "pending")];

function operation(over: Partial<ClusterOperation> = {}): ClusterOperation {
  return { id: "one", name: NAME, step: "Charts", startedAt: AT, lastSeenAt: AT, state: "running", endedAt: null, steps: STEPS, etaSeconds: null, ...over };
}

async function send(operations: ClusterOperation[]) {
  await act(async () => {
    FakeSocket.exact(MONITOR_WS).open();
    FakeSocket.exact(MONITOR_WS).send({ type: "operations", operations });
  });
}

const list = (name = NAME) => screen.getByRole("list", { name: `Steps of ${name}` });
const row = (name: string, within_ = NAME) => within(list(within_)).getByText(name).closest("li")!;
const announced = () => document.querySelector(".ds-operations-announce")!.textContent;

describe("REQ-BT-054 operation checklist", () => {
  beforeEach(() => {
    useFakeSockets();
    vi.spyOn(Date, "now").mockReturnValue(NOW);
    installApi({ "GET /api/me": { body: USERS.olivia }, "GET /api/batches": { body: [] } });
  });
  afterEach(() => void vi.restoreAllMocks());

  it("REQ-BT-054 every step shows from the start, none ticked", async () => {
    renderApp("/signin");
    await send([operation({ steps: [step("Build images", "pending"), step("Charts", "pending")] })]);
    expect(within(list()).getAllByRole("listitem")).toHaveLength(2);
    expect(row("Build images")).toHaveTextContent("Not started");
    expect(row("Charts")).toHaveTextContent("Not started");
  });

  it("REQ-BT-054 each state reads in words, with its own mark", async () => {
    renderApp("/signin");
    await send([operation()]);
    expect(row("Build images")).toHaveTextContent("Done");
    expect(row("Charts")).toHaveTextContent("Running, 1 min 30 s");
    expect(row("Smoke test")).toHaveTextContent("Not started");
    const marks = ["Build images", "Charts", "Smoke test"].map((n) => row(n).querySelector(".ds-step-mark")!.innerHTML);
    expect(new Set(marks).size).toBe(3);
  });

  it("REQ-BT-054 the running step carries the only spinner", async () => {
    renderApp("/signin");
    await send([operation()]);
    expect(screen.getByText(NAME).closest(".ds-operation")!.querySelectorAll(".ds-spinner")).toHaveLength(1);
    expect(row("Charts").querySelector(".ds-spinner")).not.toBeNull();
  });

  it("REQ-BT-054 a failed step says it failed and later steps stay unticked", async () => {
    renderApp("/signin");
    await send([operation({ state: "failed", endedAt: AT, steps: [step("Build images", "done"), step("Charts", "failed"), step("Smoke test", "pending")] })]);
    expect(row("Charts")).toHaveTextContent("Failed after 1 min");
    expect(row("Smoke test")).toHaveTextContent("Not started");
    expect(screen.getByText(`${NAME} failed`).closest(".ds-operation")!.querySelector(".ds-error-detail")).toHaveTextContent("Stopped at step Charts");
  });

  // STEPS ended a step 30 s ago, and the estimate was made then.
  it.each([
    [210, "About 3 min left"],
    [240, "About 4 min left"],
    [50, "About 1 min left"],
    [31, "About 1 min left"],
    [30, "Almost done"],
    [5, "Almost done"],
  ])("REQ-BT-054 %i seconds at the last step end reads as %s", async (etaSeconds, text) => {
    renderApp("/signin");
    await send([operation({ etaSeconds })]);
    expect(screen.getByText(text)).toBeInTheDocument();
  });

  it("REQ-BT-054 with no step ended the estimate counts from the start", async () => {
    renderApp("/signin");
    await send([operation({ etaSeconds: 150, steps: [step("Charts", "running")] })]);
    expect(screen.getByText("About 1 min left")).toBeInTheDocument();
  });

  it.each([[null], [undefined]])("REQ-BT-054 no estimate reads as none, for %s", async (etaSeconds) => {
    renderApp("/signin");
    await send([operation({ etaSeconds })]);
    expect(screen.getByText("No estimate yet")).toBeInTheDocument();
  });

  it("REQ-BT-054 finished work has no estimate", async () => {
    renderApp("/signin");
    await send([operation({ state: "done", endedAt: AT, etaSeconds: 60, steps: [step("Build images", "done")] })]);
    expect(screen.queryByText(/min left|No estimate/)).toBeNull();
  });

  it.each([[undefined], [[]]])("REQ-BT-054 without steps the toast stays one line, for %j", async (steps) => {
    renderApp("/signin");
    await send([operation({ steps, etaSeconds: 60 })]);
    expect(screen.getByText("Charts")).toBeInTheDocument();
    expect(screen.queryByRole("list", { name: `Steps of ${NAME}` })).toBeNull();
    expect(screen.queryByText(/min left|No estimate/)).toBeNull();
  });

  it("REQ-BT-054 a finished operation stays after the monitor drops it, until seen", async () => {
    renderApp("/signin");
    await send([operation({ state: "done", endedAt: AT, steps: [step("Build images", "done")] })]);
    await send([]);
    expect(row("Build images")).toHaveTextContent("Done");
    await userEvent.click(await screen.findByRole("button", { name: /^Seen/ }));
    expect(screen.queryByRole("list", { name: `Steps of ${NAME}` })).toBeNull();
    await send([operation({ state: "done", endedAt: AT, steps: [step("Build images", "done")] })]);
    expect(screen.queryByRole("list", { name: `Steps of ${NAME}` })).toBeNull();
  });

  it("REQ-BT-054 two runs with steps each keep their own list, and steplessness still joins", async () => {
    renderApp("/signin");
    await send([operation(), operation({ id: "two", name: "Restart worker" }), operation({ id: "three", name: "Roll api", steps: undefined }), operation({ id: "four", name: "Roll web", steps: undefined })]);
    expect(list()).toBeInTheDocument();
    expect(list("Restart worker")).toBeInTheDocument();
    expect(screen.getByText("2 operations running")).toBeInTheDocument();
  });

  it("REQ-BT-054 no signal stops the clock and the estimate", async () => {
    renderApp("/signin");
    await send([operation({ state: "no-signal", etaSeconds: 60 })]);
    expect(row("Charts")).toHaveTextContent("State unknown");
    expect(row("Charts")).not.toHaveTextContent("Running");
    expect(screen.queryByText(/min left|No estimate/)).toBeNull();
  });

  it("REQ-BT-054 a lost socket stops the clock and the estimate", async () => {
    renderApp("/signin");
    await send([operation({ etaSeconds: 60 })]);
    await act(async () => FakeSocket.exact(MONITOR_WS).drop());
    expect(await screen.findByText("Cluster work state unknown")).toBeInTheDocument();
    expect(row("Charts")).toHaveTextContent("State unknown");
    expect(screen.queryByText(/min left|No estimate/)).toBeNull();
  });
});

describe("REQ-BT-054 announcing a tick", () => {
  beforeEach(() => {
    useFakeSockets();
    vi.spyOn(Date, "now").mockReturnValue(NOW);
    installApi({ "GET /api/me": { body: USERS.olivia }, "GET /api/batches": { body: [] } });
  });
  afterEach(() => void vi.restoreAllMocks());

  const at = (a: OperationStep["state"], b: OperationStep["state"]) => operation({ steps: [step("Build images", a), step("Charts", b)] });

  it("REQ-BT-054 the tick goes through the live region and the list stays quiet", async () => {
    renderApp("/signin");
    await send([at("running", "pending")]);
    await send([at("done", "running")]);
    const region = document.querySelector(".ds-operations")!;
    expect(region).toHaveAttribute("role", "status");
    expect(region).toHaveAttribute("aria-atomic", "false");
    expect(region).toContainElement(document.querySelector<HTMLElement>(".ds-operations-announce"));
    expect(announced()).toBe("Build images finished.");
    expect(list()).toHaveAttribute("aria-live", "off");
  });

  it("REQ-BT-054 each step announces once, even when a frame repeats", async () => {
    renderApp("/signin");
    await send([at("running", "pending")]);
    await send([at("done", "running")]);
    await send([at("done", "running")]);
    await send([at("done", "done")]);
    expect(announced()).toBe("Charts finished.");
    await send([at("done", "running")]);
    await send([at("done", "done")]);
    expect(announced()).toBe("Charts finished.");
  });

  it("REQ-BT-054 a failure is announced as a failure", async () => {
    renderApp("/signin");
    await send([at("done", "running")]);
    await send([at("done", "failed")]);
    expect(announced()).toBe("Charts failed.");
  });

  it("REQ-BT-054 steps finished before the page opened are not read out", async () => {
    renderApp("/signin");
    await send([at("done", "running")]);
    expect(announced()).toBe("");
  });

  it("REQ-BT-054 steps finished in one frame are read together", async () => {
    renderApp("/signin");
    await send([at("running", "pending")]);
    await send([at("done", "done")]);
    expect(announced()).toBe("Build images finished. Charts finished.");
  });
});
