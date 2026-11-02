import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { MonitorReport, PodReport } from "../../../api/monitorTypes";
import { parseReport } from "../../../logic/feed/hops";
import { REPORT } from "../../../mocks/fixtures";
import { WorkersPanel } from "./WorkersPanel";

const pod = (name: string, more: Partial<PodReport> = {}): PodReport => ({ namespace: "batch-tracker", name, node: "k3s-node1", phase: "Running", ready: true, cpuLimitMilli: 200, memoryLimitMi: 192, cpuUsageMilli: 1, memoryUsageMi: 1, ...more });

const PODS = [
  pod("worker-5d9-aaaaa"),
  pod("worker-5d9-bbbbb", { ready: false }),
  pod("worker-node2-6f8-ccccc", { node: "k3s-node2" }),
  pod("dispatcher-1-ddddd"),
  pod("dispatcher-1-eeeee"),
  pod("dispatcher-node2-1-fffff", { node: "k3s-node2", ready: false }),
  pod("api-1-ggggg"),
];

const BUSY = "Busy in the last minute";
const IDLE = "Idle in the last minute";
const UNKNOWN = "Busy not known";
const known = [pod("worker-1-aaaaa", { busy: true }), pod("worker-1-bbbbb", { busy: false }), pod("worker-node2-1-ccccc", { busy: true })];

const report = (over: Partial<MonitorReport> = {}): MonitorReport => ({ ...REPORT, pods: PODS, dispatcherLeader: "dispatcher-1-ddddd", ...over });
const show = (value: MonitorReport | null, fresh = true, lost = false) => render(<WorkersPanel report={value} fresh={fresh} lost={lost} />);
const figure = (label: string) => screen.getByText(label).closest("div")!;
const workers = () => within(screen.getByRole("list", { name: "Workers" })).queryAllByRole("listitem");

describe("the Workers card", () => {
  it("REQ-BT-026 one element per Worker pod, each with its node and state, and no other service", () => {
    show(report());
    expect(workers().map((w) => w.textContent)).toEqual([expect.stringMatching(/aaaaa.*k3s-node1.*Ready/), expect.stringMatching(/bbbbb.*k3s-node1.*Not ready/), expect.stringMatching(/ccccc.*k3s-node2.*Ready/)]);
  });

  it("REQ-BT-026 a Worker that is not serving reads differently in more than colour", () => {
    show(report());
    const [up, down] = workers();
    expect(up).toHaveAttribute("data-tone", "success");
    expect(down).toHaveAttribute("data-tone", "warning");
    expect(up!.querySelector("[data-icon]")?.getAttribute("data-icon")).not.toBe(down!.querySelector("[data-icon]")?.getAttribute("data-icon"));
  });

  it("REQ-BT-026 running counts only Workers serving, not every pod in the list", () => {
    show(report());
    expect(figure("Running")).toHaveTextContent("2");
  });

  it("REQ-BT-026 a terminating Worker is drawn and is not counted as running", () => {
    show(report({ pods: [pod("worker-1-aaaaa", { terminating: true })] }));
    expect(workers()[0]).toHaveTextContent("Terminating");
    expect(figure("Running")).toHaveTextContent("0");
  });

  it("REQ-BT-026 the most possible is the report's maximum", () => {
    show(report({ workers: { max: 3 } }));
    expect(figure("Most possible")).toHaveTextContent("3");
  });

  it.each([[null], [undefined]])("REQ-BT-026 a maximum the report does not give reads Unknown, never 0 (%s)", (workers) => {
    show(report({ workers }));
    expect(figure("Most possible")).toHaveTextContent("Unknown");
  });

  it("REQ-BT-026 busy and idle count the Workers running, by what the report says of each", () => {
    show(report({ pods: known }));
    expect(figure(BUSY)).toHaveTextContent(/^.*2$/);
    expect(figure(IDLE)).toHaveTextContent(/^.*1$/);
    expect(figure(UNKNOWN)).toHaveTextContent(/^.*0$/);
  });

  it("REQ-BT-026 a Worker not running counts as neither busy nor idle", () => {
    show(report({ pods: [...known, pod("worker-1-ddddd", { ready: false, busy: false }), pod("worker-1-eeeee", { terminating: true, busy: true })] }));
    expect(figure(BUSY)).toHaveTextContent(/^.*2$/);
    expect(figure(IDLE)).toHaveTextContent(/^.*1$/);
  });

  it("REQ-BT-026 a running Worker with no busy value is not guessed, and the counts say they are not the whole", () => {
    show(report({ pods: [pod("worker-1-aaaaa", { busy: true }), pod("worker-1-bbbbb", { busy: false }), pod("worker-1-ccccc", { busy: null })] }));
    expect(figure(BUSY)).toHaveTextContent("At least 1");
    expect(figure(IDLE)).toHaveTextContent("At least 1");
    expect(figure(UNKNOWN)).toHaveTextContent(/^.*1$/);
  });

  it.each([[null], [undefined]])("REQ-BT-026 with no busy value on any Worker neither count is given (%s)", (busy) => {
    show(report({ pods: [pod("worker-1-aaaaa", { busy }), pod("worker-1-bbbbb", { busy })] }));
    expect(figure(BUSY)).toHaveTextContent("Unknown");
    expect(figure(IDLE)).toHaveTextContent("Unknown");
    expect(figure(UNKNOWN)).toHaveTextContent(/^.*2$/);
  });

  it("REQ-BT-026 each Worker says its own busy state in words and icon, and the words name the last minute", () => {
    show(report({ pods: [pod("worker-1-aaaaa", { busy: true }), pod("worker-1-bbbbb", { busy: false }), pod("worker-1-ccccc", { busy: null })] }));
    const blocks = workers();
    expect(blocks[0]).toHaveTextContent("Working in the last minute");
    expect(blocks[1]).toHaveTextContent("No work in the last minute");
    expect(blocks[2]).toHaveTextContent("Busy unknown");
    const icons = blocks.map((b) => b.querySelector("[data-detail-icon]")?.getAttribute("data-detail-icon"));
    expect(new Set(icons).size).toBe(3);
  });

  it("REQ-BT-026 a stale report keeps busy as last known and gives no count", () => {
    show(report({ pods: known, workers: { max: 3 } }), false);
    for (const label of [BUSY, IDLE, UNKNOWN, "Most possible"]) expect(figure(label)).toHaveTextContent("Stale");
    expect(workers()[0]).toHaveTextContent("Last known: working in the last minute");
    expect(workers()[0]!.querySelector("[data-detail-icon]")).toHaveAttribute("data-detail-icon", "clock");
  });

  it("REQ-BT-026 the frame parser keeps both fields and reads a missing maximum as unknown", () => {
    const frame = { checkedAt: "2026-11-01T10:00:00Z", healthy: true, checks: [], pods: [pod("worker-1-aaaaa", { busy: false })] };
    const parsed = parseReport(JSON.stringify({ ...frame, workers: { max: 3 } }));
    expect(parsed?.workers).toEqual({ max: 3 });
    expect(parsed?.pods?.[0]?.busy).toBe(false);
    expect(parseReport(JSON.stringify(frame))?.workers).toBeNull();
    expect(parseReport(JSON.stringify({ ...frame, workers: null }))?.workers).toBeNull();
    expect(parseReport(JSON.stringify({ ...frame, workers: { max: "3" } }))?.workers).toBeNull();
  });

  it("REQ-BT-026 the leader comes from the report and the standbys are the other Dispatchers serving", () => {
    show(report());
    expect(figure("Dispatcher leader")).toHaveTextContent("ddddd");
    expect(figure("Standbys")).toHaveTextContent("1: eeeee");
  });

  it("REQ-BT-026 with no leader in the report nobody is called a standby", () => {
    show(report({ dispatcherLeader: null }));
    expect(figure("Dispatcher leader")).toHaveTextContent("None reported");
    expect(figure("Standbys")).toHaveTextContent("Unknown");
  });

  it("REQ-BT-026 a report with no Worker among its pods reads 0 and says so", () => {
    show(report({ pods: [pod("api-1-ggggg")] }));
    expect(figure("Running")).toHaveTextContent("0");
    expect(screen.getByText("No Worker pods in the report")).toBeInTheDocument();
    expect(screen.queryByRole("list", { name: "Workers" })).toBeNull();
  });

  it("REQ-BT-026 a report with no pods is not read as zero Workers", () => {
    show(report({ pods: [] }));
    expect(screen.getByText("No pods in the monitor report")).toBeInTheDocument();
    expect(screen.queryByText("Running")).toBeNull();
  });

  it("REQ-BT-026 before the first report the card waits and claims nothing", () => {
    show(null);
    expect(screen.getByLabelText("Waiting for the first monitor report")).toBeInTheDocument();
    expect(screen.queryByText("Running")).toBeNull();
  });

  it("REQ-BT-026 with the monitor lost and no report the card says unknown", () => {
    show(null, false, true);
    expect(screen.getByText("Unknown")).toBeInTheDocument();
    expect(screen.queryByText("Running")).toBeNull();
  });

  it("REQ-BT-030 a report gone stale keeps its Workers as last known and reads ready nowhere", () => {
    show(report(), false);
    expect(screen.getByText(/^Last known values from /)).toBeInTheDocument();
    expect(figure("Running")).toHaveTextContent("Stale");
    expect(figure("Dispatcher leader")).toHaveTextContent("Stale");
    expect(workers()[0]).toHaveTextContent("Was ready");
    expect(workers()[0]).toHaveAttribute("data-tone", "neutral");
  });
});
