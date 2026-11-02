import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import type { PodReport } from "../../api/monitorTypes";
import { LOAD_BALANCER_ADDRESS } from "../../config";
import { PART_INFO, infoFor, padKey, plateKey, podKey, stationKey } from "./partInfo";
import { SERVICES } from "./pods";
import { buildTerrain, type TerrainMap } from "./terrain";

const SHOWN = ["batch-tracker", "ingress-nginx", "prometheus", "keda", "metallb-system"];

const caps = JSON.parse(readFileSync(resolve(__dirname, "../../../../../deploy/caps.json"), "utf8")) as { pods: { name: string; namespace?: string; node?: string }[] };

const pod = (name: string, node: string, namespace = "batch-tracker"): PodReport => ({ namespace, name, node, phase: "Running", ready: true, cpuLimitMilli: 1, memoryLimitMi: 1, cpuUsageMilli: 1, memoryUsageMi: 1 });

/** One pod for everything the deploy file says can run, so a new workload fails here until it has words. */
const everyPod = caps.pods.filter((p) => SHOWN.includes(p.namespace ?? "")).map((p) => pod(`${p.name}-abc12-xyz34`, p.node === "k3s-node2" ? "k3s-node2" : "k3s-node1", p.namespace));

const keysOf = (map: TerrainMap) => [
  ...map.plates.map((p) => plateKey(p.name)),
  ...map.blocks.map((b) => (b.kind === "pod" ? podKey(b.name) : b.kind === "pad" ? padKey(b.label) : stationKey(b.hop))),
];

describe("part words", () => {
  it("REQ-BT-028 the list of workloads is the real one", () => {
    expect(everyPod.length).toBeGreaterThan(15);
    expect(everyPod.map((p) => p.name).join(" ")).toContain("metallb-speaker");
  });

  it.each([
    ["mongo", false],
    ["cosmos", false],
    [null, false],
    ["mongo", true],
    ["cosmos", true],
    [null, true],
  ])("REQ-BT-028 every part drawn has words: store %s, compact %s", (store, compact) => {
    const missing = [...new Set(keysOf(buildTerrain(everyPod, compact, store)))].filter((key) => !PART_INFO[key]);
    expect(missing).toEqual([]);
  });

  it.each([[null], [[]]])("REQ-BT-028 every part drawn has words before a report: pods %j", (pods) => {
    const missing = keysOf(buildTerrain(pods, false, null)).filter((key) => !PART_INFO[key]);
    expect(missing).toEqual([]);
  });

  it("REQ-BT-028 every service the map knows has words", () => {
    expect(SERVICES.filter((service) => !PART_INFO[service])).toEqual([]);
  });

  it("REQ-BT-028 every entry says what it is and what it does, with no empty line", () => {
    for (const [key, info] of Object.entries(PART_INFO)) {
      expect(info.name, key).not.toBe("");
      expect(info.what, key).toMatch(/\.$/);
      expect(info.does, key).toMatch(/\.$/);
    }
  });

  it("REQ-BT-028 the two MetalLB pods and the address use the configured address", () => {
    expect(podKey("metallb-controller-abc")).toBe("metallb-controller");
    expect(podKey("metallb-speaker-abc")).toBe("metallb-speaker");
    expect(PART_INFO["metallb-controller"]!.does).toContain(LOAD_BALANCER_ADDRESS);
    expect(padKey(LOAD_BALANCER_ADDRESS)).toBe("balancer");
    expect(plateKey("Load balancer")).toBe("balancer");
  });

  it("REQ-BT-028 a smoke run of the nightly job reads as the nightly job", () => {
    expect(podKey("smoke-nightly-abc")).toBe("nightly");
    expect(podKey("nightly-29-abc")).toBe("nightly");
  });

  it("REQ-BT-028 a machine nobody wrote words for says so, under its own name", () => {
    expect(infoFor(plateKey("k3s-k9"), "k3s-k9")).toEqual({ name: "k3s-k9", what: "A part the report names.", does: "Nobody has written what it does yet." });
    expect(infoFor(plateKey("k3s-node2"), "k3s-node2").name).toBe("node2");
  });

  it("REQ-BT-028 a pod nobody wrote words for gets the plain entry, not nothing", () => {
    expect(podKey("mystery-1")).toBe("other");
  });
});
