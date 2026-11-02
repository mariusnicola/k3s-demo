import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import type { PodReport } from "../../api/monitorTypes";
import type { HopName } from "../../api/types";
import { ArchitectureDiagram, PartTip } from "../index";

const zero = { api: 0, dispatcher: 0, bus: 0, deliver: 0, worker: 0, record: 0, settle: 0 } as Record<HopName, number>;
const unknown = { state: "unknown", text: "No report" } as const;
const health = { api: { state: "ok", text: "2 of 2 pods ready" }, cosmos: unknown, dispatcher: unknown, bus: unknown, worker: unknown } as const;
const pod = (name: string, node: string, more: Partial<PodReport> = {}): PodReport => ({ namespace: "batch-tracker", name, node, phase: "Running", ready: true, cpuLimitMilli: 1, memoryLimitMi: 1, cpuUsageMilli: 1, memoryUsageMi: 1, ...more });
const PODS = [pod("api-0", "k3s-node1"), pod("worker-node2-abc12", "k3s-node2", { ready: false }), pod("dispatcher-abc12", "k3s-node1")];

function show(extra: Partial<Parameters<typeof ArchitectureDiagram>[0]> = {}) {
  return render(<ArchitectureDiagram getFrame={() => []} counts={zero} failures={zero} health={health} summary="Flow" pods={PODS} store="mongo" {...extra} />);
}

/** A part is named by the words it shows, so the name starts with its label. */
const part = (name: string | RegExp) => screen.getAllByRole("button", { name: typeof name === "string" ? new RegExp(`^${name}`) : name })[0]!;
const tip = () => screen.queryByRole("tooltip");

describe("map tips", () => {
  it("REQ-BT-028 the map is a named group of parts, each one reachable", () => {
    show();
    expect(screen.getByRole("group", { name: /^Flow/ })).toBeInTheDocument();
    for (const name of ["This page", "Line", "Monitor", "k3s-node1", "k3s-node2", "Service Bus", "Service Bus SQL", "API", "MongoDB"]) expect(part(name)).toHaveAttribute("tabindex", "0");
  });

  it("REQ-BT-028 hover opens the name, what it is and what it does, and leaving closes it", async () => {
    show();
    fireEvent.mouseOver(part("Line"));
    const shown = tip()!;
    expect(within(shown).getByText("Line")).toBeInTheDocument();
    expect(shown).toHaveTextContent("Simulator that creates batches and walks them through the steps.");
    expect(shown).toHaveTextContent("The lever sets its speed.");
    fireEvent.mouseOut(part("Line"));
    await waitFor(() => expect(tip()).toBeNull());
  });

  it("REQ-BT-028 moving from a part onto its tip keeps it open", async () => {
    show();
    fireEvent.mouseOver(part("Line"));
    fireEvent.mouseOut(part("Line"));
    fireEvent.mouseEnter(tip()!);
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(tip()).not.toBeNull();
  });

  it("REQ-BT-028 keyboard focus opens it and leaving closes it", async () => {
    show();
    await userEvent.tab();
    expect(document.activeElement).toHaveAttribute("data-part");
    expect(tip()).not.toBeNull();
    fireEvent.blur(document.activeElement!);
    await waitFor(() => expect(tip()).toBeNull());
  });

  it("REQ-BT-028 a tap opens it, a second tap closes it, a tap elsewhere closes it", async () => {
    show();
    fireEvent.click(part("Line"));
    expect(tip()).toHaveTextContent("Simulator");
    fireEvent.click(part("Line"));
    expect(tip()).toBeNull();
    fireEvent.click(part("Line"));
    fireEvent.pointerDown(document.body);
    expect(tip()).toBeNull();
  });

  it("REQ-BT-028 a part opened by tap does not close when the pointer leaves it", async () => {
    show();
    fireEvent.mouseOver(part("Line"));
    fireEvent.click(part("Line"));
    fireEvent.mouseOut(part("Line"));
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(tip()).not.toBeNull();
  });

  it("REQ-BT-028 Enter opens it from the keyboard and Escape dismisses it", () => {
    show();
    fireEvent.keyDown(part("Monitor"), { key: "Enter" });
    expect(tip()).toHaveTextContent("Watcher outside the cluster.");
    fireEvent.keyDown(document, { key: "Escape" });
    expect(tip()).toBeNull();
  });

  it("REQ-BT-028 each part carries its words as an accessible description", () => {
    show();
    expect(part("Monitor")).toHaveAccessibleDescription(/Watcher outside the cluster.*sends the report this map is drawn from/i);
  });

  it("REQ-BT-028 a pod's live line gives its state and node, from the report", () => {
    show();
    fireEvent.mouseOver(part(/^worker-node2-abc12/));
    expect(tip()).toHaveTextContent("Worker");
    expect(tip()).toHaveTextContent("worker-node2-abc12: Not ready, on k3s-node2");
  });

  it("REQ-BT-028 the Dispatcher leader says so", () => {
    show({ sender: "dispatcher-abc12", leader: true });
    fireEvent.mouseOver(part(/^dispatcher-abc12/));
    expect(tip()).toHaveTextContent("dispatcher-abc12: Ready, on k3s-node1, leader");
  });

  it("REQ-BT-030 a stale report makes the live line last known", () => {
    show({ stale: true });
    fireEvent.mouseOver(part(/^worker-node2-abc12/));
    expect(tip()).toHaveTextContent("Last known: worker-node2-abc12: Not ready, on k3s-node2");
  });

  it("REQ-BT-028 a station's live line is what the map says of it", () => {
    show();
    fireEvent.mouseOver(part("API"));
    expect(tip()).toHaveTextContent("0 accepted, 2 of 2 pods ready");
  });

  it("REQ-BT-028 the store says which one the report names", () => {
    show();
    fireEvent.mouseOver(part("MongoDB"));
    expect(tip()).toHaveTextContent("The report names MongoDB.");
  });

  it("REQ-BT-028 with no store named, none is claimed", () => {
    show({ store: null });
    fireEvent.mouseOver(part("Store"));
    expect(tip()).not.toHaveTextContent("The report names");
  });

  it("REQ-BT-028 a part with no live data has no live line, not a guess", () => {
    show();
    fireEvent.mouseOver(part("This page"));
    expect(tip()!.querySelector("[data-live]")).toBeNull();
  });

  it("REQ-BT-028 a tip follows the report while open", () => {
    const { rerender } = show();
    fireEvent.mouseOver(part(/^worker-node2-abc12/));
    rerender(<ArchitectureDiagram getFrame={() => []} counts={zero} failures={zero} health={health} summary="Flow" pods={[PODS[0]!, pod("worker-node2-abc12", "k3s-node2", { ready: true })]} store="mongo" />);
    expect(tip()).toHaveTextContent("worker-node2-abc12: Ready, on k3s-node2");
  });

  it("REQ-BT-028 a tip is drawn outside the map, so the map's edge cannot cut it", () => {
    const { container } = show();
    fireEvent.mouseOver(part("Line"));
    expect(container.contains(tip())).toBe(false);
  });

  it("REQ-BT-028 PartTip alone shows in place, with its live line", () => {
    const { container } = render(<PartTip id="t" info={{ name: "Worker", what: "A thing.", does: "It works." }} live="Ready" />);
    expect(container.contains(tip())).toBe(true);
    expect(tip()!.querySelector("[data-live]")).toHaveTextContent("Ready");
  });
});

afterEach(() => void vi.restoreAllMocks());
