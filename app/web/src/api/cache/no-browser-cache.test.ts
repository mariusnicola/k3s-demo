import { afterEach, describe, expect, it, vi } from "vitest";
import { client } from "../client";

type Route = keyof typeof client;

const FILTERS = { source: "", level: "", reason: "", farm: "", batch: "", from: "", to: "", correlationId: "", reportId: "" } as never;

/** One call per route, so a route added to the client fails here until it is listed. */
const CALLS: Record<Route, () => Promise<unknown>> = {
  devLogin: () => client.devLogin("olivia"),
  logout: () => client.logout(),
  me: () => client.me(),
  restoreSession: () => client.restoreSession(),
  listBatches: () => client.listBatches({ limit: 1 } as never),
  createBatch: () => client.createBatch("farm-a", "Vaccine"),
  getBatch: () => client.getBatch("farm-a", "b-1"),
  advance: () => client.advance("farm-a", "b-1", "Dose1", '"v1"'),
  inspect: () => client.inspect("farm-a", "b-1", true, null, '"v1"'),
  failNext: () => client.failNext("farm-a", "b-1"),
  replay: () => client.replay("farm-a", "b-1"),
  runCompliance: () => client.runCompliance(),
  metricsSeries: () => client.metricsSeries("1h"),
  lineLogs: () => client.lineLogs(FILTERS),
  serviceLogs: () => client.serviceLogs(FILTERS),
  ingressLogs: () => client.ingressLogs("c-1"),
  lineStart: () => client.lineStart(1),
  lineStop: () => client.lineStop(),
};

afterEach(() => void vi.unstubAllGlobals());

describe("B-089 a reply never comes from the browser's HTTP cache", () => {
  it("B-089 every client route is listed here", () => {
    expect(Object.keys(CALLS).sort()).toEqual(Object.keys(client).sort());
  });

  it.each(Object.keys(CALLS) as Route[])("B-089 %s asks the network and stores nothing", async (route) => {
    const fetchStub = vi.fn(async () => new Response("{}"));
    vi.stubGlobal("fetch", fetchStub);
    await CALLS[route]();
    expect(fetchStub).toHaveBeenCalledTimes(1);
    expect((fetchStub.mock.calls[0] as unknown[])[1]).toMatchObject({ cache: "no-store" });
  });
});
