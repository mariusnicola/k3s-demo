import type { StationName } from "../../api/types";
import { LOAD_BALANCER_ADDRESS } from "../../config";
import { podLabel, podService } from "./pods";

export interface PartInfo {
  name: string;
  what: string;
  does: string;
}

/** The words for every part of the map, one entry per part. */
export const PART_INFO: Record<string, PartInfo> = {
  page: { name: "This page", what: "Your screen.", does: "It talks to the system through the load balancer address, like any user." },
  browser: { name: "Browser", what: "Where this page runs, outside the cluster.", does: "It reaches the system the way any user does." },
  balancer: { name: "Load balancer", what: `${LOAD_BALANCER_ADDRESS}, the single address everyone connects to.`, does: "MetalLB holds it and hands traffic to the ingress. It does not share anything out." },
  docker: { name: "Docker", what: "Containers on the PC, outside the cluster.", does: "They hold what the cluster calls on: the store, Service Bus, the Line and Monitor." },
  node1: { name: "Node1", what: "The main machine, running in Docker on the PC.", does: "It holds the app: API, web, Live, Workers, Dispatchers and the nightly job." },
  node2: { name: "node2", what: "The second machine.", does: "It holds the ingress, Prometheus, KEDA and the MetalLB controller, plus more Workers and one Dispatcher." },
  cluster: { name: "Cluster", what: "The Kubernetes cluster.", does: "It stands as one plate until a report names its machines." },
  unplaced: { name: "Not placed", what: "Pods no machine has taken yet.", does: "They run nowhere until the cluster places them." },
  line: { name: "Line", what: "Simulator that creates batches and walks them through the steps.", does: "The lever sets its speed." },
  monitor: { name: "Monitor", what: "Watcher outside the cluster.", does: "It checks health, reads the pods and sends the report this map is drawn from." },
  metallb: {
    name: "MetalLB",
    what: "Gives the cluster a real address on the local network, the job a cloud load balancer does.",
    does: `The controller assigns ${LOAD_BALANCER_ADDRESS}. The speaker on the ingress's machine tells the network the address is there.`,
  },
  "metallb-controller": { name: "MetalLB controller", what: "The part of MetalLB that hands out addresses.", does: `It assigns ${LOAD_BALANCER_ADDRESS}.` },
  "metallb-speaker": { name: "MetalLB speaker", what: "The part of MetalLB on each machine.", does: "Only the speaker on the ingress's machine tells the network the address is there." },
  ingress: { name: "Ingress", what: "The front door, one nginx instance on node2.", does: "It sends each request to the right service and shares it across instances. The Dispatcher reaches Workers through it too." },
  web: { name: "web", what: "The server for the site.", does: "It serves the site's pages to the browser." },
  api: { name: "API", what: "The service users and the Line talk to.", does: "It takes their requests, checks permissions and saves batches." },
  dispatcher: { name: "Dispatcher", what: "The service that moves events. Several run: one leads, the others stand by.", does: "It picks up saved events, sends them through Service Bus and delivers each to a Worker." },
  worker: { name: "Worker", what: "The service that handles events. These scale up and down.", does: "It does the work for each event." },
  nightly: { name: "Nightly job", what: "A scheduled run.", does: "It asks a Worker to do the nightly compliance checks." },
  live: { name: "Live", what: "The service behind everything that moves on this screen.", does: "It collects what is happening and pushes it to the screen over a WebSocket, moving dots included." },
  store: { name: "Store", what: "The database holding batches and events.", does: "The API, Workers and Dispatchers read and write it." },
  "idle-store": { name: "Store not in use", what: "The other database the app can run on.", does: "It runs here with no app on it, so it has no links and no counts." },
  bus: { name: "Service Bus", what: "The queue events pass through.", does: "The Dispatcher sends each event here and takes it back to deliver, so events wait safely on the way to a Worker." },
  "bus-sql": { name: "Service Bus SQL", what: "The SQL database the local Service Bus emulator needs.", does: "Our code does not use it directly." },
  prometheus: { name: "Prometheus", what: "The collector of numbers from the services.", does: "It feeds the charts and the scaler." },
  keda: { name: "KEDA", what: "The scaler.", does: "It reads the request rate from Prometheus, and CPU, and tells the cluster when to add or remove API and Worker pods." },
  netprobe: { name: "netprobe", what: "A small checker, one per machine.", does: "It checks the link between the machines." },
  other: { name: "Other pods", what: "Pods that are neither the app's main services nor the front door.", does: "Each block names what it is, such as web, Prometheus, KEDA or netprobe." },
};

const PLATE_KEYS: Record<string, string> = { "k3s-node1": "node1", "k3s-node2": "node2", Cluster: "cluster", "Not placed": "unplaced", Docker: "docker", "Load balancer": "balancer", Browser: "browser" };
const PAD_KEYS: Record<string, string> = {
  [LOAD_BALANCER_ADDRESS]: "balancer",
  "This page": "page",
  Line: "line",
  Monitor: "monitor",
  "Nightly job": "nightly",
  "Service Bus SQL": "bus-sql",
  MongoDB: "idle-store",
  "Cosmos DB": "idle-store",
  API: "api",
  Dispatcher: "dispatcher",
  Worker: "worker",
  Live: "live",
  Ingress: "ingress",
  MetalLB: "metallb",
  "Other pods": "other",
};
const STATION_KEYS: Record<StationName, string> = { api: "api", cosmos: "store", dispatcher: "dispatcher", bus: "bus", worker: "worker" };

/** A new plate or lane comes back as itself, which has no entry, and the test of every drawn part fails. */
export const plateKey = (name: string) => PLATE_KEYS[name] ?? name;
export const padKey = (label: string) => PAD_KEYS[label] ?? label;
export const stationKey = (hop: StationName) => STATION_KEYS[hop];

/** A machine or container added after these words were written still answers, without inventing what it does. */
export const infoFor = (key: string, name: string): PartInfo => PART_INFO[key] ?? { name, what: "A part the report names.", does: "Nobody has written what it does yet." };

export function podKey(name: string): string {
  if (/^(smoke-)?nightly-/.test(name)) return "nightly";
  const service = podService(name);
  if (service === "metallb") return /^metallb-(controller|speaker)-/.exec(name)?.[0].slice(0, -1) ?? "metallb";
  if (service !== "other") return service;
  const label = podLabel(name, service);
  return PART_INFO[label] ? label : "other";
}
