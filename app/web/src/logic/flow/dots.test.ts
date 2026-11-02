import { HOP_NAMES } from "../../api/types";
import { hop } from "../../mocks/fixtures";
import { DotField, LINGER_MS, MAX_DOTS, TRAVEL_MS } from "./dots";

describe("DotField", () => {
  it("REQ-BT-028 a trace walks its real hops in order", () => {
    const field = new DotField();
    field.ingest(
      HOP_NAMES.map((name) => hop({ hop: name })),
      0,
    );
    const seen = [field.frame(0)[0]?.to];
    for (let i = 1; i < HOP_NAMES.length; i += 1) seen.push(field.frame(i * TRAVEL_MS)[0]?.to);
    expect(seen).toEqual(["cosmos", "dispatcher", "bus", "dispatcher", "worker", "cosmos", "dispatcher"]);
  });

  it("REQ-BT-028 an API hop walks on to the store, through the pod that took it", () => {
    const field = new DotField();
    field.ingest([hop({ hop: "api", pod: "api-a" }), hop({ hop: "dispatcher" })], 0);
    expect(field.frame(0)[0]).toMatchObject({ from: "client", to: "cosmos", through: "api-a" });
    expect(field.frame(TRAVEL_MS)[0]).toMatchObject({ from: "cosmos", to: "dispatcher", through: null });
  });

  it("REQ-BT-028 a hop that names its pod sends the dot to that pod", () => {
    const field = new DotField();
    field.ingest([hop({ hop: "bus" }), hop({ hop: "worker", pod: "worker-a" }), hop({ hop: "record" })], 0);
    expect(field.frame(0)[0]).toMatchObject({ to: "bus", toPod: null });
    expect(field.frame(TRAVEL_MS)[0]).toMatchObject({ from: "dispatcher", fromPod: null, to: "worker", toPod: "worker-a" });
    expect(field.frame(2 * TRAVEL_MS)[0]).toMatchObject({ from: "worker", fromPod: "worker-a", to: "cosmos", toPod: null });
  });

  it("REQ-BT-028 a dot leaves after its last hop", () => {
    const field = new DotField();
    field.ingest([hop()], 0);
    expect(field.frame(TRAVEL_MS)).toHaveLength(1);
    expect(field.frame(TRAVEL_MS + LINGER_MS)).toHaveLength(0);
  });

  it("REQ-BT-028 a failed hop marks the dot for the rest of its walk", () => {
    const field = new DotField();
    field.ingest([hop({ hop: "bus" }), hop({ hop: "worker", ok: false }), hop({ hop: "record" })], 0);
    field.frame(TRAVEL_MS);
    expect(field.frame(2 * TRAVEL_MS)[0]).toMatchObject({ to: "cosmos", ok: false });
  });

  it("REQ-BT-028 zero hops draws nothing", () => {
    const field = new DotField();
    field.ingest([], 0);
    expect(field.frame(100)).toEqual([]);
  });

  it("REQ-BT-028 500 hops in a tick stay within the dot limit and count the rest", () => {
    const field = new DotField();
    field.ingest(
      Array.from({ length: 500 }, (_, i) => hop({ traceId: `t${i}` })),
      0,
    );
    expect(field.frame(0).length).toBeLessThanOrEqual(MAX_DOTS);
    expect(field.undrawn).toBe(500 - MAX_DOTS);
  });

  it("REQ-BT-028 undrawn count clears when dots free up", () => {
    const field = new DotField(1);
    field.ingest([hop({ traceId: "a" }), hop({ traceId: "b" })], 0);
    expect(field.undrawn).toBe(1);
    field.frame(10_000);
    expect(field.undrawn).toBe(0);
  });
});
