import type { HopStation, StationName } from "../../api/types";
import { LOAD_BALANCER_ADDRESS } from "../../config";
import { elbow, pointOnRoute, type Point } from "./geometry";
import type { PodService } from "./pods";
import type { TerrainMap, TerrainPad, TerrainPod } from "./terrain";

export interface TerrainLink {
  id: string;
  /** Path through the anchors, along the plate axes where there is room. */
  d: string;
  /** Its corners, so the picture can be sized to hold them. */
  points: Point[];
}

type Places = Pick<TerrainMap, "stations" | "pods" | "pads">;
type End = [id: string, anchor: Point];

const RUN_IN_PODS: ReadonlySet<HopStation> = new Set<HopStation>(["api", "dispatcher", "worker"]);

const service = (map: Places, name: PodService): TerrainPod[] => map.pods.filter((p) => p.service === name);
const pad = (map: Places, label: string): TerrainPad | undefined => map.pads.find((p) => p.label === label);
const ingressOf = (map: Places): Point | undefined => service(map, "ingress")[0]?.anchor;
const balancerOf = (map: Places): Point | undefined => pad(map, LOAD_BALANCER_ADDRESS)?.anchor;

/** Every client comes in at the load balancer's address. Only hops that happen in a pod land on one: a store hop names the pod that sent it. */
export function hopAnchor(map: Places, station: HopStation, pod: string | null | undefined): Point {
  if (station === "client") return balancerOf(map) ?? ingressOf(map) ?? map.stations.api.anchor;
  const own = pod && RUN_IN_PODS.has(station) ? map.pods.find((p) => p.name === pod) : undefined;
  return own?.anchor ?? map.stations[station].anchor;
}

interface Leg {
  from: HopStation | null;
  fromPod?: string | null;
  to: HopStation;
  toPod?: string | null;
  through?: string | null;
}

/** A client passes the ingress on its way to the API. The Dispatcher reaches a Worker through the ingress too, by its in-cluster name, so never by the load balancer. `sender` stands in when a hop names no Dispatcher pod. */
export function legPath(map: Places, leg: Leg, sender?: string | null): Point[] {
  const pod = (station: HopStation, named?: string | null) => named ?? (station === "dispatcher" ? sender : null);
  const to = hopAnchor(map, leg.to, pod(leg.to, leg.toPod));
  if (!leg.from) return [to];
  const from = hopAnchor(map, leg.from, pod(leg.from, leg.fromPod));
  const internal = (leg.from === "dispatcher" && leg.to === "worker") || (leg.from === "worker" && leg.to === "dispatcher");
  const entering = leg.from === "client";
  const between = internal || entering ? ingressOf(map) : undefined;
  const api = entering && leg.to === "cosmos" ? hopAnchor(map, "api", leg.through) : undefined;
  return [from, between, api, to].filter((point) => point !== undefined);
}

/** Where a dot is at `t` of a whole leg, each part of the path taking an equal share. */
export function pointOnPath(path: readonly Point[], t: number, turns: boolean): Point {
  if (path.length < 2) return path[0]!;
  const parts = path.length - 1;
  const part = Math.min(Math.floor(t * parts), parts - 1);
  return pointOnRoute(path[part]!, path[part + 1]!, t * parts - part, turns);
}

function pathBetween(from: Point, to: Point, turns: boolean): Pick<TerrainLink, "d" | "points"> {
  const points = turns ? [from, elbow(from, to), to] : [from, to];
  return { d: points.map((p, i) => `${i === 0 ? "M" : "L"}${p.x.toFixed(1)} ${p.y.toFixed(1)}`).join(" "), points };
}

const pods = (map: Places, name: PodService): End[] => service(map, name).map((p) => [p.name, p.anchor]);
const named = (id: string, anchor: Point | undefined): End[] => (anchor ? [[id, anchor]] : []);
const station = (map: Places, name: StationName): End[] => [[name, map.stations[name].anchor]];

/** The connections the services really hold. Links never move: only a dot, from a real hop, does. */
export function buildLinks(map: Places, turns: boolean): TerrainLink[] {
  const ingress = named("ingress", ingressOf(map));
  const balancer = named("lb", balancerOf(map));
  const ingressNode = service(map, "ingress")[0]?.plate;
  // With externalTrafficPolicy Local, only the speaker on the ingress pod's node answers for the address.
  const metallb: End[] = service(map, "metallb").filter((p) => !p.name.includes("speaker") || p.plate === ingressNode).map((p) => [p.name, p.anchor]);
  const web: End[] = map.pods.filter((p) => p.name.startsWith("web-")).map((p) => [p.name, p.anchor]);
  const [line, monitor] = [named("line", pad(map, "Line")?.anchor), named("monitor", pad(map, "Monitor")?.anchor)];
  const live = named("live", service(map, "live")[0]?.anchor);
  const [api, dispatcher, worker] = [pods(map, "api"), pods(map, "dispatcher"), pods(map, "worker")];
  const [cosmos, bus] = [station(map, "cosmos"), station(map, "bus")];
  const pairs: [End[], End[]][] = [
    [[...line, ...monitor, ...named("browser", pad(map, "This page")?.anchor), ...metallb], balancer],
    [balancer, ingress],
    [ingress, [...api, ...web, ...line, ...monitor]],
    [api, cosmos],
    [dispatcher, cosmos],
    [dispatcher, bus],
    [named("sql", pad(map, "Service Bus SQL")?.anchor), bus],
    [dispatcher, ingress],
    [ingress, worker],
    [worker, cosmos],
    [named("nightly", pad(map, "Nightly job")?.anchor), worker],
    [[...api, ...dispatcher, ...worker], live],
    [live, ingress],
  ];
  return pairs.flatMap(([starts, ends]) => starts.flatMap(([a, from]) => ends.map(([b, to]) => ({ id: `${a}>${b}`, ...pathBetween(from, to, turns) }))));
}
