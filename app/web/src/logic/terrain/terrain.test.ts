
import { type PodReport } from "../../api/monitorTypes";
import { buildTerrain, podSummary, type TerrainMap, type TerrainPod } from "./terrain";
import { elbow, iso, pointOnRoute } from "./geometry";
import { podService, podState } from "./pods";

const pod = (name: string, node: string, more: Partial<PodReport> = {}): PodReport => ({
  namespace: "batch-tracker",
  name,
  node,
  phase: "Running",
  ready: true,
  cpuLimitMilli: 200,
  memoryLimitMi: 192,
  cpuUsageMilli: 10,
  memoryUsageMi: 50,
  ...more,
});
const POD = (map: TerrainMap, name: string): TerrainPod => map.pods.find((p) => p.name === name)!;
const CLUSTER = [pod("api-5d9-aaaaa", "k3s-node1"), pod("worker-7c4-bbbbb", "k3s-node1"), pod("worker-node2-6f8-ccccc", "k3s-node2"), pod("dispatcher-node2-5b7-ddddd", "k3s-node2")];

describe("isometric projection", () => {
  it("REQ-BT-028 moving equally along both ground axes goes straight down the screen", () => {
    expect(iso(3, 3).x).toBeCloseTo(iso(0, 0).x, 5);
    expect(iso(3, 3).y).toBeGreaterThan(iso(0, 0).y);
  });

  it("REQ-BT-028 height moves straight up the screen", () => {
    expect(iso(2, 1, 1).x).toBeCloseTo(iso(2, 1, 0).x, 5);
    expect(iso(2, 1, 1).y).toBeLessThan(iso(2, 1, 0).y);
  });
});

describe("one object per pod", () => {
  it("REQ-BT-028 each pod in the report is one object, and only the app namespace is drawn", () => {
    const map = buildTerrain([...CLUSTER, pod("coredns-1", "k3s-node1", { namespace: "kube-system" })], false);
    expect(map.pods.map((p) => p.name).sort()).toEqual(CLUSTER.map((p) => p.name).sort());
  });

  it("REQ-BT-028 a scale up adds one object and a scale down removes one", () => {
    const more = [...CLUSTER, pod("worker-7c4-eeeee", "k3s-node1")];
    expect(buildTerrain(more, false).pods).toHaveLength(CLUSTER.length + 1);
    expect(buildTerrain(CLUSTER.slice(1), false).pods).toHaveLength(CLUSTER.length - 1);
  });

  it("REQ-BT-028 a pod stands on its real node", () => {
    const map = buildTerrain(CLUSTER, false);
    expect(POD(map, "worker-7c4-bbbbb").plate).toBe("k3s-node1");
    expect(POD(map, "worker-node2-6f8-ccccc").plate).toBe("k3s-node2");
    expect(POD(map, "dispatcher-node2-5b7-ddddd").plate).toBe("k3s-node2");
  });

  it("REQ-BT-028 a pod with no node stands on its own plate and says so", () => {
    const map = buildTerrain([pod("worker-7c4-fffff", "", { phase: "Pending", ready: false })], false);
    expect(POD(map, "worker-7c4-fffff").plate).toBe("Not placed");
    expect(map.plates.map((p) => p.name)).toContain("Not placed");
  });

  it("REQ-BT-028 the service comes from the pod name, a second deployment included", () => {
    expect(podService("api-5d9-aaaaa")).toBe("api");
    expect(podService("worker-node2-6f8-ccccc")).toBe("worker");
    expect(podService("dispatcher-5b7-ddddd")).toBe("dispatcher");
    expect(podService("live-1")).toBe("live");
    expect(podService("workshop-1")).toBe("other");
    expect(podService("netprobe-worker-1")).toBe("other");
  });

  it("REQ-BT-028 forty workers wrap on their plate and no two objects share ground", () => {
    const workers = Array.from({ length: 40 }, (_, i) => pod(`worker-7c4-${i}`, "k3s-node1"));
    for (const compact of [false, true]) {
      const { blocks } = buildTerrain([...workers, pod("api-1", "k3s-node2")], compact);
      for (const [i, a] of blocks.entries()) {
        for (const b of blocks.slice(i + 1)) {
          const apart = a.ground.x + a.ground.w <= b.ground.x || b.ground.x + b.ground.w <= a.ground.x || a.ground.y + a.ground.d <= b.ground.y || b.ground.y + b.ground.d <= a.ground.y;
          expect(apart).toBe(true);
        }
      }
    }
  });
});

describe("pod states read apart", () => {
  it.each([
    [{ ready: true }, "ready"],
    [{ ready: false }, "not-ready"],
    [{ ready: undefined }, "not-reported"],
    [{ phase: "Pending", ready: false }, "pending"],
    [{ phase: "Failed", ready: false }, "failed"],
    [{ phase: "Unknown", ready: false }, "unknown"],
    [{ ready: true, terminating: true }, "terminating"],
  ] as [Partial<PodReport>, string][])("REQ-BT-030 %j is %s", (more, state) => {
    expect(podState(pod("api-1", "k3s-node2", more))).toBe(state);
  });

  it("REQ-BT-030 a pod that is not serving stands lower than one that is, so height tells them apart without colour", () => {
    const map = buildTerrain([pod("api-1", "k3s-node2"), pod("api-2", "k3s-node2", { ready: false }), pod("api-3", "k3s-node2", { ready: true, terminating: true })], false);
    expect(POD(map, "api-2").height).toBeLessThan(POD(map, "api-1").height);
    expect(POD(map, "api-3").height).toBeLessThan(POD(map, "api-2").height);
  });
});

describe("stations", () => {
  it("REQ-BT-028 every hop has one station, with the stores on the Docker plate", () => {
    const map = buildTerrain(CLUSTER, false);
    expect(Object.keys(map.stations).sort()).toEqual(["api", "bus", "cosmos", "dispatcher", "worker"]);
    expect(map.stations.cosmos.plate).toBe("Docker");
    expect(map.stations.bus.plate).toBe("Docker");
  });

  it("REQ-BT-028 a service station stands on the node with most of its pods", () => {
    const map = buildTerrain([pod("worker-a", "k3s-node1"), pod("worker-node2-b", "k3s-node2"), pod("worker-node2-c", "k3s-node2")], false);
    expect(map.stations.worker.plate).toBe("k3s-node2");
  });

  it("REQ-BT-028 a service spread over two nodes is labelled on both", () => {
    const map = buildTerrain(CLUSTER, false);
    expect(map.pads).toContainEqual(expect.objectContaining({ label: "Worker", plate: "k3s-node2" }));
  });

  it("REQ-BT-028 with no report the services still draw, on one plate that says what it is", () => {
    const map = buildTerrain(null, false);
    expect(map.pods).toEqual([]);
    expect(map.plates.map((p) => p.name)).toEqual(["Cluster", "Docker", "Load balancer", "Browser"]);
    expect(Object.keys(map.stations)).toHaveLength(5);
  });

  it("REQ-BT-028 a route leg runs along a plate axis, never a loose diagonal", () => {
    const from = iso(2, 3);
    const to = iso(9, 12);
    const corner = elbow(from, to);
    expect(corner.x).toBeCloseTo(iso(9, 3).x, 0);
    expect(corner.y).toBeCloseTo(iso(9, 3).y, 0);
  });

  it("REQ-BT-028 a dot follows the route: start, corner at halfway, end", () => {
    const from = iso(2, 3);
    const to = iso(9, 12);
    expect(pointOnRoute(from, to, 0)).toEqual(from);
    expect(pointOnRoute(from, to, 0.5)).toEqual(elbow(from, to));
    expect(pointOnRoute(from, to, 1)).toEqual(to);
  });
});

describe("the picture", () => {
  it("REQ-BT-028 lanes on a plate stand far enough apart for the screen height of a block", () => {
    const { blocks } = buildTerrain(CLUSTER, false);
    const heads = blocks.filter((b) => b.kind !== "pod" && b.plate === "k3s-node2").sort((a, b) => a.ground.y - b.ground.y);
    expect(heads[1]!.ground.y - heads[0]!.ground.y).toBeGreaterThanOrEqual(3.4);
  });

  it("REQ-BT-028 nearer objects paint later", () => {
    const { blocks } = buildTerrain(CLUSTER, false);
    const depth = blocks.map((b) => b.ground.x + b.ground.y);
    expect(depth).toEqual([...depth].sort((a, b) => a - b));
  });

  it.each([false, true])("REQ-BT-038 every anchor sits inside the picture, compact %s", (compact) => {
    const map = buildTerrain(CLUSTER, compact);
    const { x, y, width, height } = map.viewBox;
    for (const a of [...Object.values(map.stations).map((s) => s.anchor), ...map.pods.map((p) => p.anchor)]) {
      expect(a.x).toBeGreaterThanOrEqual(x);
      expect(a.x).toBeLessThanOrEqual(x + width);
      expect(a.y).toBeGreaterThanOrEqual(y);
      expect(a.y).toBeLessThanOrEqual(y + height);
    }
  });

  it.each([false, true])("REQ-BT-038 every link stays inside the picture, compact %s", (compact) => {
    const { links, viewBox } = buildTerrain(CLUSTER, compact);
    for (const step of links.flatMap((l) => l.d.split(/[ML]/).filter(Boolean))) {
      const [x, y] = step.trim().split(" ").map(Number) as [number, number];
      expect(x).toBeGreaterThanOrEqual(viewBox.x);
      expect(x).toBeLessThanOrEqual(viewBox.x + viewBox.width);
      expect(y).toBeGreaterThanOrEqual(viewBox.y);
      expect(y).toBeLessThanOrEqual(viewBox.y + viewBox.height);
    }
  });

  it("REQ-BT-038 the compact route runs straight, because its plates stack down the screen", () => {
    const from = iso(2, 3);
    const to = iso(9, 12);
    const middle = pointOnRoute(from, to, 0.5, false);
    expect(middle.x).toBeCloseTo((from.x + to.x) / 2, 0);
    expect(middle.y).toBeCloseTo((from.y + to.y) / 2, 0);
    for (const link of buildTerrain(CLUSTER, true).links) expect(link.d.split(/[ML]/).filter(Boolean)).toHaveLength(2);
  });

  it("REQ-BT-038 the compact picture is narrow enough to read at 360 px", () => {
    const wide = buildTerrain(CLUSTER, false);
    const compact = buildTerrain(CLUSTER, true);
    expect(compact.viewBox.width).toBeLessThanOrEqual(620);
    expect(compact.viewBox.width).toBeLessThan(wide.viewBox.width);
    expect(compact.viewBox.height).toBeGreaterThan(compact.viewBox.width);
  });
});

describe("text alternative", () => {
  it("REQ-BT-028 names each node with what stands on it, and every pod not serving", () => {
    const text = podSummary(buildTerrain([...CLUSTER, pod("worker-7c4-eeeee", "k3s-node1", { ready: false })], false));
    expect(text).toContain("k3s-node1: API 1 pod, Worker 2 pods.");
    expect(text).toContain("k3s-node2: Dispatcher 1 pod, Worker 1 pod.");
    expect(text).toContain("worker-7c4-eeeee is not ready.");
  });

  it("REQ-BT-028 says nothing about pods when there are none to describe", () => {
    expect(podSummary(buildTerrain(null, false))).toBe("");
  });
});
