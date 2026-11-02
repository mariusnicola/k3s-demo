import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import { vi } from "vitest";
import { MONITOR_WS } from "../../../config";
import { formatCount } from "../../../logic/format";
import type { Batch } from "../../../api/types";
import { REPORT, batch, hopSnapshot, lineSnapshot } from "../../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Handler, type Reply } from "../../../mocks/harness";

function dashboard(extra: Record<string, Reply | Handler> = {}, rows: Batch[] = [batch()], line = true) {
  const calls = installApi({ "GET /api/me": meRoute("ada"), ...extra });
  if (line) FakeSocket.greet("/line/ws", lineSnapshot());
  FakeSocket.greet("/ws/hops", hopSnapshot({ batches: rows.map((row) => ({ etag: '"v1"', batch: row })) }));
  renderApp("/dashboard");
  return calls;
}

beforeEach(() => useFakeSockets());

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("cluster panels", () => {
  it("REQ-BT-026 shows failing checks, nodes, pods and caps with a breach", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() =>
      socket.send({
        ...REPORT,
        healthy: false,
        checks: [{ name: "cosmos", healthy: false, error: "timeout" }, ...REPORT.checks.slice(1)],
        caps: [{ node: "k3s-node2", resource: "memoryMi", cap: 100, sum: 120, breach: true }],
        capIssues: ["api-0: limit differs from caps.json"],
      }),
    );
    expect(await screen.findByText("1 of 2 checks failing")).toBeInTheDocument();
    expect(screen.getByText("timeout")).toBeInTheDocument();
    expect(screen.getByText("Breach: over the cap")).toBeInTheDocument();
    expect(screen.getByText("k3s-node2 memory")).toBeInTheDocument();
    expect(screen.getByText("Live limits differ from caps.json")).toBeInTheDocument();
    expect(screen.getByText("api-0: limit differs from caps.json")).toBeInTheDocument();
    expect(screen.getByText("api-0")).toBeInTheDocument();
    expect(screen.getByText("k3s-node2")).toBeInTheDocument();
  });

  it("REQ-BT-026 a full cap bar says it counts limits, and live use stands under it", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() => socket.send({ ...REPORT, caps: [{ node: "k3s-node2", resource: "cpuMilli", cap: 4000, sum: 4000, breach: false }] }));
    const panel = await screen.findByRole("region", { name: "Node caps" });
    expect(within(panel).getByText("k3s-node2 CPU reserved")).toBeInTheDocument();
    expect(within(panel).getByText(`${formatCount(4000)} of ${formatCount(4000)} m, 0 m free`)).toBeInTheDocument();
    expect(within(panel).getByText(`In use now: ${formatCount(1200)} m`)).toBeInTheDocument();
    expect(within(panel).getByText(/not live use/)).toBeInTheDocument();
    act(() => socket.drop());
    expect(await within(panel).findByText(`Last known use: ${formatCount(1200)} m`)).toBeInTheDocument();
    expect(within(panel).getByText(/not live use/)).toBeInTheDocument();
  });

  it("REQ-BT-030 a breach on lost data reads as past, not current", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() => socket.send({ ...REPORT, caps: [{ node: "k3s-node2", resource: "cpuMilli", cap: 100, sum: 120, breach: true }] }));
    expect(await screen.findByText("Breach: over the cap")).toBeInTheDocument();
    act(() => socket.drop());
    expect(await screen.findByText("Was over the cap")).toBeInTheDocument();
    expect(screen.queryByText("Breach: over the cap")).toBeNull();
  });

  it("REQ-BT-036 a report without cluster sections says so", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() => socket.send({ checkedAt: new Date().toISOString(), healthy: true, checks: [] }));
    expect(await screen.findByText("No nodes in the monitor report")).toBeInTheDocument();
    expect(screen.getByText("No caps in the monitor report")).toBeInTheDocument();
    expect(within(screen.getByRole("region", { name: "Pods" })).getByText("No pods in the monitor report")).toBeInTheDocument();
    expect(within(screen.getByRole("region", { name: "Workers" })).getByText("No pods in the monitor report")).toBeInTheDocument();
  });

  it("REQ-BT-036 before the first report panels show loading", async () => {
    dashboard();
    expect((await screen.findAllByRole("status", { name: "Waiting for the first monitor report" })).length).toBeGreaterThan(0);
  });
});

describe("cluster map", () => {
  it("REQ-BT-028 the map draws one object per pod from the monitor report", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    const worker = { ...REPORT.pods![0]!, name: "worker-node2-a", node: "k3s-node2" };
    act(() => socket.send({ ...REPORT, checkedAt: new Date().toISOString(), pods: [REPORT.pods![0]!, worker] }));
    await waitFor(() => expect(document.querySelectorAll("[data-pod]")).toHaveLength(2));
    expect(document.querySelector('[data-pod="worker-node2-a"]')).toHaveAttribute("data-plate", "k3s-node2");
  });

  it("REQ-BT-028 a report tells how many checks pass, large and beside the map", async () => {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() => socket.send({ ...REPORT, checkedAt: new Date().toISOString() }));
    const stat = (await screen.findByText("Checks healthy")).closest(".ds-stat")!;
    expect(stat).toHaveTextContent("2 of 2");
    act(() => socket.drop());
    await waitFor(() => expect(stat).toHaveTextContent("Unknown"));
  });
});

describe("pods table", () => {
  const pod = (name: string, phase: string, ready?: boolean, more: object = {}) => ({ namespace: "batch-tracker", name, node: "k3s-node2", phase, ready, cpuLimitMilli: 500, memoryLimitMi: 512, cpuUsageMilli: 40, memoryUsageMi: 180, ...more });
  const FAILED = { node: "", cpuUsageMilli: null, memoryUsageMi: null };
  // The column header says Ready too.
  const BADGE = { selector: ".ds-badge span" };

  async function table(pods: object[]) {
    dashboard();
    await screen.findByRole("heading", { name: "Architecture" });
    const socket = FakeSocket.exact(MONITOR_WS);
    socket.open();
    act(() => socket.send({ ...REPORT, checkedAt: new Date().toISOString(), pods }));
    return { socket, pods: await screen.findByRole("table", { name: "Pods" }) };
  }
  const tone = (element: HTMLElement) => element.closest(".ds-badge")!.getAttribute("data-tone");
  const rows = (pods: HTMLElement) => within(pods).getAllByRole("row").slice(1).map((r) => r.textContent ?? "");

  it("REQ-BT-030 ready and not ready read apart, and only ready is green", async () => {
    const { pods } = await table([pod("api-0", "Running", true), pod("api-1", "Running", false)]);
    expect(tone(within(pods).getByText("Ready", BADGE))).toBe("success");
    expect(tone(within(pods).getByText("Not ready"))).toBe("warning");
    for (const phase of within(pods).getAllByText("Running")) expect(tone(phase)).toBe("neutral");
  });

  it("REQ-BT-030 a report with no ready field says so, never a guess", async () => {
    const { pods } = await table([pod("api-0", "Running")]);
    expect(within(pods).getByText("Not reported")).toBeInTheDocument();
    expect(within(pods).queryByText("Ready", BADGE)).toBeNull();
    expect(within(pods).queryByText("Not ready")).toBeNull();
  });

  it("REQ-BT-030 a Failed pod is danger, not placed, its usage unknown, and leads the table", async () => {
    const { pods } = await table([pod("api-0", "Running", true), pod("job-1", "Failed", false, FAILED)]);
    expect(tone(within(pods).getByText("Failed"))).toBe("danger");
    const [first, second] = rows(pods);
    expect(first).toContain("job-1");
    expect(first).toContain("Not placed");
    expect(first).toContain("Unknown of 500 m");
    expect(second).toContain("api-0");
  });

  it("REQ-BT-030 failed pods are named above the table", async () => {
    await table([pod("api-0", "Running", true), pod("job-1", "Failed", false, FAILED), pod("job-2", "Failed", false, FAILED)]);
    expect(await screen.findByText("2 pods failed")).toBeInTheDocument();
    expect(screen.getByText("job-1, job-2")).toBeInTheDocument();
  });

  it("REQ-BT-030 one failed pod reads in the singular, and none shows no notice", async () => {
    await table([pod("job-1", "Failed", false, FAILED)]);
    expect(await screen.findByText("1 pod failed")).toBeInTheDocument();
    cleanup();
    FakeSocket.reset();
    await table([pod("api-0", "Running", true)]);
    expect(screen.queryByText(/pods? failed/)).toBeNull();
  });

  it("REQ-BT-030 an Unknown pod is a warning and sits after Failed, before the rest", async () => {
    const { pods } = await table([pod("api-0", "Running", true), pod("api-1", "Unknown", false), pod("job-1", "Failed", false, FAILED)]);
    expect(tone(within(pods).getByText("Unknown"))).toBe("warning");
    expect(rows(pods).map((r) => r.slice(0, 5))).toEqual(["job-1", "api-1", "api-0"]);
  });

  it("REQ-BT-030 a pod not ready sits above ready pods", async () => {
    const { pods } = await table([pod("api-0", "Running", true), pod("api-1", "Pending", false)]);
    expect(rows(pods).map((r) => r.slice(0, 5))).toEqual(["api-1", "api-0"]);
  });

  it("REQ-BT-030 lost data reads as past: no live tone, no failed notice", async () => {
    const { socket, pods } = await table([pod("job-1", "Failed", false, FAILED), pod("api-0", "Running", true)]);
    expect(await screen.findByText("1 pod failed")).toBeInTheDocument();
    act(() => socket.drop());
    const failed = await within(pods).findByText("Was Failed");
    expect(tone(failed)).toBe("neutral");
    expect(tone(within(pods).getByText("Was ready"))).toBe("neutral");
    expect(tone(within(pods).getByText("Was not ready"))).toBe("neutral");
    expect(screen.queryByText("1 pod failed")).toBeNull();
  });
});
