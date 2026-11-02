import { Profiler } from "react";
import { render, screen } from "@testing-library/react";
import { vi } from "vitest";
import type { HopName } from "../../api/types";
import type { PodReport } from "../../api/monitorTypes";
import type { DotFrame } from "../../logic/flow/dots";
import { ArchitectureDiagram, FlowMap } from "../index";

describe("ArchitectureDiagram", () => {
  const zero = { api: 0, dispatcher: 0, bus: 0, deliver: 0, worker: 0, record: 0, settle: 0 } as Record<HopName, number>;
  const unknown = { state: "unknown", text: "Stale" } as const;
  const health = { api: { state: "ok", text: "2 of 2 pods ready" }, cosmos: { state: "failing", text: "Failing" }, dispatcher: unknown, bus: unknown, worker: unknown } as const;

  function mockMotion(reduced: boolean) {
    vi.spyOn(window, "matchMedia").mockImplementation(
      (query: string) =>
        ({
          matches: reduced && query.includes("reduced-motion"),
          media: query,
          addEventListener: () => undefined,
          removeEventListener: () => undefined,
        }) as unknown as MediaQueryList,
    );
  }

  afterEach(() => void vi.restoreAllMocks());

  it("REQ-BT-028 draws a dot at the position of its frame", async () => {
    mockMotion(false);
    const { container } = render(<ArchitectureDiagram getFrame={() => [{ traceId: "t1", from: null, to: "api", progress: 1, ok: true }]} counts={zero} failures={zero} health={health} summary="s" />);
    await vi.waitFor(() => expect(container.querySelector('.ds-dot[data-trace="t1"]')).toHaveAttribute("visibility", "visible"));
  });

  it("REQ-BT-028 a failed trace is marked by shape data, not colour alone", async () => {
    mockMotion(false);
    const { container } = render(<ArchitectureDiagram getFrame={() => [{ traceId: "t1", from: "bus", to: "worker", progress: 0.5, ok: false }]} counts={zero} failures={zero} health={health} summary="s" />);
    await vi.waitFor(() => expect(container.querySelector('.ds-dot[data-ok="false"]')).not.toBeNull());
    expect(container.querySelector(".ds-dot-fail")).not.toBeNull();
  });

  it("REQ-BT-028 reduced motion places dots at the station, without tween or pulse", async () => {
    mockMotion(true);
    const { container } = render(<ArchitectureDiagram getFrame={() => [{ traceId: "t1", from: "api", to: "cosmos", progress: 0.1, ok: true }]} counts={zero} failures={zero} health={health} summary="s" />);
    expect(container.querySelector("figure")).toHaveAttribute("data-reduced", "true");
    const anchor = container.querySelector('[data-station="cosmos"]')!.getAttribute("data-anchor")!;
    await vi.waitFor(() => expect(position(container, "t1")).toBe(anchor));
  });

  const nextFrames = () => new Promise<void>((done) => requestAnimationFrame(() => requestAnimationFrame(() => requestAnimationFrame(() => done()))));
  const watchWrites = (container: HTMLElement) => {
    const seen: MutationRecord[] = [];
    new MutationObserver((records) => seen.push(...records)).observe(container, { subtree: true, attributes: true });
    return seen;
  };

  it("REQ-BT-028 a map with nothing to draw writes nothing, frame after frame", async () => {
    mockMotion(false);
    const { container } = render(<ArchitectureDiagram getFrame={() => []} counts={zero} failures={zero} health={health} summary="s" />);
    await nextFrames();
    const writes = watchWrites(container);
    await nextFrames();
    expect(writes).toEqual([]);
  });

  it("REQ-BT-028 a dot that ends is hidden once", async () => {
    mockMotion(false);
    let frames: DotFrame[] = [{ traceId: "t1", from: null, to: "api", progress: 1, ok: true }];
    const { container } = render(<ArchitectureDiagram getFrame={() => frames} counts={zero} failures={zero} health={health} summary="s" />);
    await vi.waitFor(() => expect(container.querySelector('.ds-dot[data-trace="t1"]')).toHaveAttribute("visibility", "visible"));
    frames = [];
    const writes = watchWrites(container);
    await nextFrames();
    expect(writes.filter((r) => r.attributeName === "visibility")).toHaveLength(1);
    expect(container.querySelector('.ds-dot[data-trace="t1"]')).toHaveAttribute("visibility", "hidden");
  });

  it("REQ-BT-028 tells what it shows in text", () => {
    mockMotion(false);
    render(<ArchitectureDiagram getFrame={() => []} counts={{ ...zero, api: 12 }} failures={zero} health={health} summary="Flow summary" />);
    expect(screen.getByRole("group", { name: "Flow summary" })).toBeInTheDocument();
  });

  const pod = (name: string, node: string, more: Partial<PodReport> = {}): PodReport => ({ namespace: "batch-tracker", name, node, phase: "Running", ready: true, cpuLimitMilli: 200, memoryLimitMi: 192, cpuUsageMilli: 1, memoryUsageMi: 1, ...more });
  const position = (container: HTMLElement, trace: string) => /translate\(([^)]+)\)/.exec(container.querySelector(`.ds-dot[data-trace="${trace}"]`)?.getAttribute("transform") ?? "")?.[1];
  const props = { counts: zero, failures: zero, health, summary: "Flow" };

  it("REQ-BT-028 one object per pod, on its node, and a scale down takes one away", () => {
    mockMotion(false);
    const two = [pod("worker-a", "k3s-node1"), pod("worker-node2-b", "k3s-node2")];
    const { container, rerender } = render(<ArchitectureDiagram getFrame={() => []} pods={two} {...props} />);
    expect(container.querySelectorAll("[data-pod]")).toHaveLength(2);
    expect(container.querySelector('[data-pod="worker-node2-b"]')).toHaveAttribute("data-plate", "k3s-node2");
    rerender(<ArchitectureDiagram getFrame={() => []} pods={two.slice(1)} {...props} />);
    expect(container.querySelectorAll("[data-pod]")).toHaveLength(1);
  });

  it("REQ-BT-030 a pod says its state in data and in its name, not colour alone", () => {
    mockMotion(false);
    const { container } = render(<ArchitectureDiagram getFrame={() => []} pods={[pod("api-a", "n", { ready: false }), pod("api-b", "n", { ready: true, terminating: true })]} {...props} />);
    expect(container.querySelector('[data-pod="api-a"]')).toHaveAttribute("data-state", "not-ready");
    expect(screen.getByRole("button", { name: /^api-a: Not ready/ })).toContainElement(container.querySelector('[data-pod="api-a"]'));
    expect(container.querySelector('[data-pod="api-b"]')).toHaveAttribute("data-state", "terminating");
    expect(screen.getByRole("button", { name: /^api-b: Terminating/ })).toContainElement(container.querySelector('[data-pod="api-b"]'));
  });

  it("REQ-BT-028 a dot with a pod goes to that pod", async () => {
    mockMotion(true);
    const { container } = render(<ArchitectureDiagram getFrame={() => [{ traceId: "t1", from: "bus", fromPod: null, to: "worker", toPod: "worker-a", progress: 1, ok: true }]} pods={[pod("worker-a", "n"), pod("worker-b", "n")]} {...props} />);
    const anchor = container.querySelector('[data-pod="worker-a"]')!.getAttribute("data-anchor")!;
    expect(anchor).not.toBe(container.querySelector('[data-pod="worker-b"]')!.getAttribute("data-anchor"));
    await vi.waitFor(() => expect(position(container, "t1")).toBe(anchor));
  });

  it("REQ-BT-028 a dot leaves from the pod that handled the hop before", async () => {
    mockMotion(false);
    const { container } = render(<ArchitectureDiagram getFrame={() => [{ traceId: "t1", from: "worker", fromPod: "worker-a", to: "cosmos", toPod: null, progress: 0, ok: true }]} pods={[pod("worker-a", "n"), pod("worker-b", "n")]} {...props} />);
    const anchor = container.querySelector('[data-pod="worker-a"]')!.getAttribute("data-anchor")!;
    await vi.waitFor(() => expect(position(container, "t1")).toBe(anchor));
  });

  it("REQ-BT-055 the flow map keeps its text readable on a phone", () => {
    const counts = { Created: 1, Dose1: 0, Dose2: 0, Inspected: 0, Shipped: 0, Closed: 0, Rejected: 0 };
    const { container, rerender } = render(<FlowMap counts={counts} />);
    const gap = () => {
      const [title, count] = container.querySelectorAll('[data-state-block="Created"] text');
      return Number(count!.getAttribute("y")) - Number(title!.getAttribute("y"));
    };
    const wide = gap();
    expect(container.querySelector(".ds-diagram")).not.toHaveAttribute("data-compact");
    rerender(<FlowMap counts={counts} compact />);
    expect(container.querySelector(".ds-diagram")).toHaveAttribute("data-compact");
    expect(gap()).toBeGreaterThan(wide);
  });

  it("REQ-BT-038 compact text on a station is its name alone, so no two lines can collide", () => {
    mockMotion(false);
    const { container, rerender } = render(<ArchitectureDiagram getFrame={() => []} {...props} />);
    const lines = () => container.querySelectorAll('[data-station="api"] text').length;
    expect(lines()).toBeGreaterThan(1);
    rerender(<ArchitectureDiagram getFrame={() => []} compact {...props} />);
    expect(lines()).toBe(1);
  });

  it("REQ-BT-028 a dot with no pod, or a pod no longer shown, goes to the service plate", async () => {
    mockMotion(true);
    const frames = [
      { traceId: "t1", from: "bus", fromPod: null, to: "worker", toPod: null, progress: 1, ok: true },
      { traceId: "t2", from: "bus", fromPod: null, to: "worker", toPod: "worker-gone", progress: 1, ok: true },
    ] as const;
    const { container } = render(<ArchitectureDiagram getFrame={() => [...frames]} pods={[pod("worker-a", "n")]} {...props} />);
    const anchor = container.querySelector('[data-station="worker"]')!.getAttribute("data-anchor")!;
    await vi.waitFor(() => expect(position(container, "t1")).toBe(anchor));
    expect(position(container, "t2")).toBe(anchor);
  });

  it("REQ-BT-028 dots move with no React render per frame", async () => {
    mockMotion(false);
    const onRender = vi.fn();
    let frames = 0;
    render(
      <Profiler id="map" onRender={onRender}>
        <ArchitectureDiagram
          getFrame={() => {
            frames += 1;
            return [{ traceId: "t1", from: "api", fromPod: null, to: "cosmos", toPod: null, progress: 0.5, ok: true }];
          }}
          pods={[pod("api-a", "n")]}
          {...props}
        />
      </Profiler>,
    );
    await vi.waitFor(() => expect(frames).toBeGreaterThan(3));
    expect(onRender).toHaveBeenCalledTimes(1);
  });

  it("REQ-BT-028 the text alternative covers the pods", () => {
    mockMotion(false);
    render(<ArchitectureDiagram getFrame={() => []} pods={[pod("api-a", "k3s-node2")]} {...props} />);
    expect(screen.getByRole("group", { name: /^Flow k3s-node2: API 1 pod\.$/ })).toBeInTheDocument();
  });

  it("REQ-BT-030 stale pods are marked, and with no report the map says what it is missing", () => {
    mockMotion(false);
    const { container, rerender } = render(<ArchitectureDiagram getFrame={() => []} pods={[pod("api-a", "n")]} stale {...props} />);
    expect(container.querySelector("figure")).toHaveAttribute("data-stale", "true");
    rerender(<ArchitectureDiagram getFrame={() => []} pods={null} {...props} />);
    expect(container.querySelector("figure")).not.toHaveAttribute("data-stale");
    expect(container.querySelector("[data-plate-name='Cluster']")).toHaveTextContent("Pods unknown");
  });

  it("REQ-BT-026 a station reads what the report says of it, in the report's words", () => {
    mockMotion(false);
    const { container } = render(<ArchitectureDiagram getFrame={() => []} counts={zero} failures={zero} health={health} summary="s" />);
    expect(container.querySelector('[data-station="worker"]')).toHaveTextContent("Stale");
    expect(container.querySelector('[data-station="api"]')).toHaveTextContent("2 of 2 pods ready");
    expect(container.querySelector('[data-station="cosmos"]')).toHaveTextContent("Failing");
  });

  it("REQ-BT-028 the map names the store the report names, and the one not in use carries its words and no count", () => {
    mockMotion(false);
    const counts = { ...zero, api: 5, record: 3 };
    const { container } = render(<ArchitectureDiagram getFrame={() => []} counts={counts} failures={zero} health={health} store="mongo" padWords={{ "Cosmos DB": "Answers, not in use" }} summary="s" />);
    const station = container.querySelector('[data-station="cosmos"]');
    expect(station).toHaveTextContent("MongoDB");
    expect(station).toHaveTextContent("5 saved, 3 records written");
    expect(station).not.toHaveTextContent("Cosmos DB");
    const idle = container.querySelector('[data-pad="Cosmos DB"]');
    expect(idle).toHaveTextContent("Answers, not in use");
    expect(idle).not.toHaveTextContent("saved");
  });

  it("REQ-BT-028 with no store reported the map names none", () => {
    mockMotion(false);
    const { container } = render(<ArchitectureDiagram getFrame={() => []} counts={zero} failures={zero} health={health} summary="s" />);
    expect(container.querySelector('[data-station="cosmos"]')).toHaveTextContent("Store");
    expect(container.querySelector('[data-station="cosmos"]')).not.toHaveTextContent("Cosmos DB");
  });

  it("REQ-BT-028 every number says what it counts, and the record leg shows on the store", () => {
    mockMotion(false);
    const counts = { ...zero, bus: 7, record: 3, deliver: 2 };
    const { container } = render(<ArchitectureDiagram getFrame={() => []} counts={counts} failures={zero} health={health} waiting="4 waiting, oldest 73 s" summary="s" />);
    expect(container.querySelector('[data-station="bus"]')).toHaveTextContent("7 sent to queue, 2 delivered");
    expect(container.querySelector('[data-station="bus"]')).toHaveTextContent("4 waiting, oldest 73 s");
    expect(container.querySelector('[data-station="cosmos"]')).toHaveTextContent("3 records written");
    expect(container.querySelector('[data-station="record"]')).toBeNull();
  });

  it("REQ-BT-028 playback hides the counts, because they cover only what has played", () => {
    mockMotion(false);
    const { container } = render(<ArchitectureDiagram getFrame={() => []} counts={null} failures={zero} health={health} summary="s" />);
    expect(container.querySelector('[data-station="bus"]')).not.toHaveTextContent("sent to queue");
  });

  it("REQ-BT-028 a number on a plate covers that plate's pods only", () => {
    mockMotion(false);
    const pods = [pod("dispatcher-a", "n1"), pod("dispatcher-b", "n1"), pod("dispatcher-c", "n2")];
    const { container } = render(<ArchitectureDiagram getFrame={() => []} counts={{ ...zero, dispatcher: 90 }} podCounts={{ "dispatcher-a": 2, "dispatcher-c": 88 }} failures={zero} health={health} pods={pods} sender="dispatcher-c" summary="s" />);
    expect(container.querySelector('[data-station="dispatcher"]')).toHaveTextContent("2 marked sent");
    expect(container.querySelector('[data-pad="Dispatcher"]')).toHaveTextContent("88 marked sent");
    expect(container.querySelector('[data-pod="dispatcher-c"]')).toHaveAttribute("data-sending");
    expect(container.querySelector('[data-pod="dispatcher-c"]')).toHaveTextContent("sending");
  });

  it("REQ-BT-038 on a phone the numbers leave the picture and stand under it as text", () => {
    mockMotion(false);
    const { container } = render(<ArchitectureDiagram compact getFrame={() => []} counts={{ ...zero, bus: 7 }} failures={zero} health={health} waiting="4 waiting, oldest seen 73 s" pods={[pod("worker-a", "n1"), pod("worker-b", "n1"), pod("worker-c", "n2")]} padWords={{ Line: "Answers" }} summary="s" />);
    expect(container.querySelector('[data-station="bus"]')).not.toHaveTextContent("sent to queue");
    expect(container.querySelector('[data-pad="Line"]')).not.toHaveTextContent("Answers");
    const words = container.querySelector(".ds-map-words")!;
    expect(words).toHaveTextContent("Service Bus7 sent to queue, 0 delivered, Stale, 4 waiting, oldest seen 73 s");
    expect(words).toHaveTextContent("Line on DockerAnswers");
  });

  it("REQ-BT-028 links stand still: nothing moves but a dot", () => {
    mockMotion(false);
    const { container } = render(<ArchitectureDiagram getFrame={() => []} counts={zero} failures={zero} health={health} pods={[pod("worker-a", "n")]} summary="s" />);
    expect(container.querySelector(".ds-map-track")).toBeNull();
    expect(container.querySelector('[data-link="worker-a>cosmos"]')).not.toBeNull();
  });
});
