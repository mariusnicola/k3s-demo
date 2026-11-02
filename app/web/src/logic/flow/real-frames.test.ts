import { describe, expect, it } from "vitest";
import type { Hop, HopName, HopStation } from "../../api/types";
import { podService } from "../terrain/pods";
import { buildTerrain } from "../terrain/terrain";
import { hopAnchor, legPath } from "../terrain/links";
import { DotField, TRAVEL_MS, type DotFrame } from "./dots";
import { REAL_PODS, REAL_TRACE } from "./realFrames";

const map = buildTerrain(REAL_PODS, false);
const old = (name: HopName) => REAL_TRACE.find((h) => h.hop === name)!;
const raced = { ...old("dispatcher"), at: old("api").at };
const leg = (f: DotFrame) => `${f.from ?? "start"}>${f.to}`;

const LEGS: [HopName, HopStation, HopStation][] = [
  ["api", "client", "api"],
  ["dispatcher", "cosmos", "dispatcher"],
  ["bus", "dispatcher", "bus"],
  ["deliver", "bus", "dispatcher"],
  ["worker", "dispatcher", "worker"],
  ["record", "worker", "cosmos"],
  ["settle", "worker", "dispatcher"],
];
/** The settled frame: every hop says its leg and its place in the trace. */
const FULL: Hop[] = LEGS.map(([hop, from, to], i) => ({ ...REAL_TRACE[0]!, hop, from, to, seq: i + 1, pod: `${from === "worker" ? "worker" : "dispatcher"}-a` }));

/** Plays the frames at their real times and lists every leg a dot was drawn on. */
function legsOf(hops: readonly Hop[]): string[] {
  const field = new DotField();
  const legs: string[] = [];
  const start = Date.parse(hops[0]!.at);
  const end = Date.parse(hops.at(-1)!.at) + 2 * TRAVEL_MS;
  let next = 0;
  for (let now = start; now <= end; now += 50) {
    while (next < hops.length && Date.parse(hops[next]!.at) <= now) field.ingest([hops[next++]!], now);
    for (const frame of field.frame(now)) if (!legs.includes(leg(frame))) legs.push(leg(frame));
  }
  return legs;
}

function walked(field: DotField, steps: number): string[] {
  return Array.from({ length: steps }, (_, i) => leg(field.frame(i * TRAVEL_MS)[0]!));
}

describe("REQ-BT-028 today's frames, from the running site, draw only real legs", () => {
  it("REQ-BT-028 the capture really has a wait in the queue longer than a dot lives", () => {
    expect(Date.parse(old("worker").at) - Date.parse(old("bus").at)).toBeGreaterThan(10_000);
    expect(old("bus").pod).toMatch(/^dispatcher/);
    expect(old("record").pod).toMatch(/^worker/);
  });

  it("REQ-BT-028 one step crosses every leg, and never bus straight to Worker", () => {
    expect(legsOf(REAL_TRACE)).toEqual(["client>cosmos", "cosmos>dispatcher", "dispatcher>bus", "dispatcher>worker", "worker>cosmos"]);
  });

  it("REQ-BT-028 a page opened mid-step still draws the leg into the Worker", () => {
    expect(legsOf(REAL_TRACE.slice(3))).toEqual(["dispatcher>worker", "worker>cosmos"]);
  });

  it("REQ-BT-028 hops of one timestamp that raced in one frame walk in the real order", () => {
    const field = new DotField();
    field.ingest([raced, old("api")], 0);
    expect(walked(field, 2)).toEqual(["client>cosmos", "cosmos>dispatcher"]);
  });

  it("REQ-BT-028 a hop of one timestamp that raced in a later frame never walks the dot backwards", () => {
    const field = new DotField();
    field.ingest([raced], 0);
    field.ingest([old("api")], 0);
    expect(walked(field, 2)).toEqual(["cosmos>dispatcher", "cosmos>dispatcher"]);
  });
});

describe("REQ-BT-028 the settled frame places a dot by `to`, ordered by `seq`", () => {
  it("REQ-BT-028 a dot walks all seven legs, return leg included, whatever order they arrived in", () => {
    const field = new DotField();
    field.ingest([...FULL].reverse(), 0);
    expect(walked(field, 7)).toEqual(LEGS.map(([, from, to]) => `${from}>${to}`).map((l, i) => (i === 0 ? "client>api" : l)));
  });

  it("REQ-BT-028 a redelivery sorts after the first try and shows as a new try", () => {
    const again = { ...FULL[3]!, seq: 15 };
    const field = new DotField();
    field.ingest([again, FULL[4]!, FULL[3]!], 0);
    expect(walked(field, 3)).toEqual(["bus>dispatcher", "dispatcher>worker", "bus>dispatcher"]);
  });

  it("REQ-BT-028 a hop older by `seq` that came in a later frame is not walked", () => {
    const field = new DotField();
    field.ingest([FULL[2]!], 0);
    field.ingest([FULL[1]!], 0);
    expect(walked(field, 2)).toEqual(["dispatcher>bus", "dispatcher>bus"]);
  });

  it("REQ-BT-028 a failed hop that came second still marks the dot", () => {
    const field = new DotField();
    field.ingest([FULL[2]!], 0);
    field.ingest([{ ...FULL[1]!, ok: false }], 0);
    expect(field.frame(0)[0]).toMatchObject({ ok: false });
  });

  it("REQ-BT-028 the reporter pod marks only its own end of the leg", () => {
    const field = new DotField();
    field.ingest([FULL[2]!], 0);
    expect(field.frame(0)[0]).toMatchObject({ from: "dispatcher", fromPod: "dispatcher-a", to: "bus", toPod: null });
    expect(podService("dispatcher-a")).toBe("dispatcher");
  });
});

describe("REQ-BT-028 a leg is drawn where it really runs", () => {
  const ingress = map.pods.find((p) => p.service === "ingress")!;
  const worker = map.pods.find((p) => p.name === old("worker").pod)!;
  const dispatcher = map.pods.find((p) => p.name === old("bus").pod)!;
  const dot = (from: HopStation, to: HopStation, fromPod: string | null, toPod: string | null): DotFrame => ({ traceId: "t", from, fromPod, to, toPod, progress: 0, ok: true });

  it("REQ-BT-028 a bus hop lands on Service Bus, though the dispatcher pod reported it", () => {
    expect(legPath(map, dot("dispatcher", "bus", dispatcher.name, null)).at(-1)).toEqual(map.stations.bus.anchor);
    expect(dispatcher.anchor).not.toEqual(map.stations.bus.anchor);
    expect(hopAnchor(map, "bus", dispatcher.name)).toEqual(map.stations.bus.anchor);
  });

  it("REQ-BT-028 there is no Record block: the record leg ends on Cosmos", () => {
    expect(Object.keys(map.stations).sort()).toEqual(["api", "bus", "cosmos", "dispatcher", "worker"]);
    expect(legPath(map, dot("worker", "cosmos", worker.name, null))).toEqual([worker.anchor, map.stations.cosmos.anchor]);
  });

  it("REQ-BT-028 a step goes from the load balancer through the ingress and its API pod to the store", () => {
    const balancer = map.pads.find((p) => p.plate === "Load balancer")!;
    const api = map.pods.find((p) => p.service === "api")!;
    expect(legPath(map, { ...dot("client", "cosmos", null, null), through: api.name })).toEqual([balancer.anchor, ingress.anchor, api.anchor, map.stations.cosmos.anchor]);
  });

  it("REQ-BT-028 Dispatcher to Worker and back go through the real ingress pod", () => {
    expect(legPath(map, dot("dispatcher", "worker", dispatcher.name, worker.name))).toEqual([dispatcher.anchor, ingress.anchor, worker.anchor]);
    expect(legPath(map, dot("worker", "dispatcher", worker.name, dispatcher.name))).toEqual([worker.anchor, ingress.anchor, dispatcher.anchor]);
  });

  it("REQ-BT-028 a client's request comes through the load balancer, then the ingress pod", () => {
    const balancer = map.pads.find((p) => p.plate === "Load balancer")!;
    const api = map.pods.find((p) => p.service === "api")!;
    expect(hopAnchor(map, "client", null)).toEqual(balancer.anchor);
    expect(legPath(map, dot("client", "api", null, api.name))).toEqual([balancer.anchor, ingress.anchor, api.anchor]);
  });

  it("REQ-BT-028 Dispatcher to Worker stays inside the cluster, so it never touches the load balancer", () => {
    const balancer = map.pads.find((p) => p.plate === "Load balancer")!;
    expect(legPath(map, dot("dispatcher", "worker", dispatcher.name, worker.name))).not.toContainEqual(balancer.anchor);
  });

  it("REQ-BT-028 a Worker hop lands on that pod, on its real node", () => {
    expect(hopAnchor(map, "worker", worker.name)).toEqual(worker.anchor);
    expect(worker.plate).toBe(REAL_PODS.find((p) => p.name === worker.name)!.node);
  });

  it("REQ-BT-028 a dot with no known pod falls back to the sender the map saw", () => {
    expect(legPath(map, dot("dispatcher", "worker", null, worker.name), dispatcher.name)[0]).toEqual(dispatcher.anchor);
  });
});

describe("REQ-BT-028 the map holds what really runs", () => {
  const ids = map.links.map((l) => l.id);

  it("REQ-BT-028 every shown pod stands on its real node, with ingress, Prometheus and KEDA", () => {
    const shown = REAL_PODS.filter((p) => ["batch-tracker", "ingress-nginx", "prometheus", "keda", "metallb-system"].includes(p.namespace));
    for (const space of ["ingress-nginx", "prometheus", "keda", "metallb-system"]) expect(shown.some((p) => p.namespace === space)).toBe(true);
    for (const pod of shown) expect(map.pods.find((p) => p.name === pod.name)?.plate, pod.name).toBe(pod.node);
  });

  it("REQ-BT-028 plumbing stays off the map, and each node says how many pods it really has", () => {
    expect(map.pods.some((p) => /coredns|metrics-server|local-path/.test(p.name))).toBe(false);
    for (const node of ["k3s-node1", "k3s-node2"]) {
      const total = REAL_PODS.filter((p) => p.node === node).length;
      const drawn = map.pods.filter((p) => p.plate === node).length;
      expect(map.plates.find((p) => p.name === node)!.note).toContain(`${drawn} of ${total} pods drawn`);
    }
  });

  it("REQ-BT-028 k3s-node1 says it is a Docker container", () => {
    expect(map.plates.find((p) => p.name === "k3s-node1")!.note).toContain("batchtracker-node1");
  });

  it("REQ-BT-028 Docker holds the stores, the Service Bus database, the Line and the monitor", () => {
    expect(map.pads.filter((p) => p.plate === "Docker").map((p) => p.label).sort()).toEqual(["Line", "Monitor", "Service Bus SQL"]);
  });

  it("REQ-BT-028 the browser and the nightly job have their places", () => {
    expect(map.pads.some((p) => p.plate === "Browser")).toBe(true);
    expect(map.pads.some((p) => p.label === "Nightly job")).toBe(true);
  });

  it("REQ-BT-028 a link exists per pod, on both nodes", () => {
    for (const pod of map.pods.filter((p) => p.service === "worker")) {
      expect(ids).toContain(`ingress>${pod.name}`);
      expect(ids).toContain(`${pod.name}>cosmos`);
      expect(ids).toContain(`${pod.name}>live`);
      expect(ids).toContain(`nightly>${pod.name}`);
    }
    for (const pod of map.pods.filter((p) => p.service === "dispatcher")) {
      expect(ids).toContain(`${pod.name}>bus`);
      expect(ids).toContain(`${pod.name}>ingress`);
    }
    for (const pod of map.pods.filter((p) => p.service === "api")) expect(ids).toContain(`ingress>${pod.name}`);
    expect(map.pods.some((p) => p.service === "worker" && p.plate === "k3s-node2")).toBe(true);
  });

  it.each([false, true])("REQ-BT-038 no link of the real cluster runs off the picture, compact %s", (compact) => {
    const { links, viewBox } = buildTerrain(REAL_PODS, compact);
    for (const step of links.flatMap((l) => l.d.split(/[ML]/).filter(Boolean))) {
      const [x, y] = step.trim().split(" ").map(Number) as [number, number];
      expect(x).toBeGreaterThanOrEqual(viewBox.x);
      expect(x).toBeLessThanOrEqual(viewBox.x + viewBox.width);
      expect(y).toBeGreaterThanOrEqual(viewBox.y);
      expect(y).toBeLessThanOrEqual(viewBox.y + viewBox.height);
    }
  });

  it("REQ-BT-028 the load balancer is its own object, with its address and its real pods", () => {
    const plate = map.plates.find((p) => p.name === "Load balancer")!;
    expect(plate.note).toContain("<INGRESS_IP>");
    expect(map.pads.find((p) => p.plate === "Load balancer")?.label).toBe("<INGRESS_IP>");
    expect(map.pods.filter((p) => p.service === "metallb").map((p) => p.plate).sort()).toEqual(["k3s-node1", "k3s-node2", "k3s-node2"]);
  });

  it("REQ-BT-028 every outside client reaches the ingress only through the load balancer", () => {
    for (const id of ["line>lb", "browser>lb", "monitor>lb", "lb>ingress"]) expect(ids).toContain(id);
    for (const id of ["line>ingress", "browser>ingress", "monitor>ingress"]) expect(ids).not.toContain(id);
  });

  it("REQ-BT-028 the controller gives the address, and only the speaker beside the ingress announces it", () => {
    const pods = map.pods.filter((p) => p.service === "metallb");
    const near = pods.find((p) => /speaker/.test(p.name) && p.plate === "k3s-node2")!;
    const far = pods.find((p) => /speaker/.test(p.name) && p.plate === "k3s-node1")!;
    expect(ids).toContain(`${pods.find((p) => /controller/.test(p.name))!.name}>lb`);
    expect(ids).toContain(`${near.name}>lb`);
    expect(ids).not.toContain(`${far.name}>lb`);
  });

  it("REQ-BT-028 the ingress reaches the Line, the monitor, the web pods and Live, as its rules say", () => {
    for (const id of ["ingress>line", "ingress>monitor", "live>ingress"]) expect(ids).toContain(id);
    for (const pod of map.pods.filter((p) => /^web-/.test(p.name))) expect(ids).toContain(`ingress>${pod.name}`);
    expect(ids).toContain("sql>bus");
  });
});
