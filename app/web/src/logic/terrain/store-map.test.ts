import type { Check } from "../../api/monitorTypes";
import { nodeHealth, padWords, parseReport } from "../feed/hops";
import { REPORT } from "../../mocks/fixtures";
import { buildTerrain } from "./terrain";

const passed = (name: string): Check => ({ name, healthy: true, error: null });
const failed = (name: string): Check => ({ name, healthy: false, error: "down" });
const report = (store: string | undefined, ...checks: Check[]) => ({ ...REPORT, store, checks });
const onDocker = (store: string | null) => buildTerrain(null, false, store).blocks.filter((b) => b.plate === "Docker");
const docker = (store: string | null) => onDocker(store).map((b) => (b.kind === "pod" ? b.name : b.label));

describe("the store on the map comes from the report", () => {
  it("REQ-BT-028 the store in use is the station, named by the report", () => {
    expect(buildTerrain(null, false, "mongo").stations.cosmos.label).toBe("MongoDB");
    expect(buildTerrain(null, false, "cosmos").stations.cosmos.label).toBe("Cosmos DB");
  });

  it("REQ-BT-028 the store not in use stands on the Docker plate as a plain container", () => {
    expect(docker("mongo")).toContain("Cosmos DB");
    expect(docker("mongo").filter((label) => label === "MongoDB")).toHaveLength(1);
    expect(docker("cosmos")).toContain("MongoDB");
    expect(onDocker("mongo").find((b) => b.kind === "pad" && b.label === "Cosmos DB")).toBeDefined();
  });

  it("REQ-BT-028 no link reaches the store not in use", () => {
    const { links, pads } = buildTerrain(null, false, "mongo");
    const idle = pads.find((p) => p.label === "Cosmos DB")!;
    expect(links.filter((l) => l.points.some((p) => p.x === idle.anchor.x && p.y === idle.anchor.y))).toEqual([]);
  });

  it("REQ-BT-028 a report that names no store claims neither: the station reads Store and no store stands as idle", () => {
    expect(buildTerrain(null, false, null).stations.cosmos.label).toBe("Store");
    expect(docker(null)).not.toContain("MongoDB");
    expect(docker(null)).not.toContain("Cosmos DB");
  });
});

describe("store health comes from the store in use", () => {
  it("REQ-BT-028 the station reads the check of the store in use", () => {
    expect(nodeHealth(report("mongo", passed("mongo"), failed("cosmos")), true).cosmos).toEqual({ state: "ok", text: "Healthy" });
    expect(nodeHealth(report("mongo", failed("mongo"), passed("cosmos")), true).cosmos).toEqual({ state: "failing", text: "Failing" });
  });

  it("REQ-BT-028 a passing Cosmos check says nothing of a Mongo store", () => {
    expect(nodeHealth(report("mongo", passed("cosmos")), true).cosmos).toEqual({ state: "unknown", text: "Not reported" });
  });

  it("REQ-BT-028 with no store named the station claims no health", () => {
    expect(nodeHealth(report(undefined, passed("cosmos")), true).cosmos).toEqual({ state: "unknown", text: "Store not reported" });
  });

  it("REQ-BT-028 the store not in use reads its own check and says it is not in use", () => {
    expect(padWords(report("mongo", passed("cosmos")), true)["Cosmos DB"]).toBe("Answers, not in use");
    expect(padWords(report("mongo", failed("cosmos")), true)["Cosmos DB"]).toBe("Failing");
    expect(padWords(report("mongo"), true)["Cosmos DB"]).toBe("Not reported");
    expect(padWords(report("mongo", passed("cosmos")), false)["Cosmos DB"]).toBe("Stale");
  });

  it("REQ-BT-028 with no store named no container is called idle", () => {
    expect(padWords(report(undefined, passed("cosmos")), true)).not.toHaveProperty("Cosmos DB");
    expect(padWords(report(undefined), false)).not.toHaveProperty("MongoDB");
  });

  it("REQ-BT-028 the store name survives the frame parser, and a frame with none gives null", () => {
    const frame = { checkedAt: "2026-11-01T10:00:00Z", healthy: true, checks: [] };
    expect(parseReport(JSON.stringify({ ...frame, store: "mongo" }))?.store).toBe("mongo");
    expect(parseReport(JSON.stringify(frame))?.store).toBeNull();
  });
});
