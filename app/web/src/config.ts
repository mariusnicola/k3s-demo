/** Every base URL the browser talks to. Nothing else in src builds a URL host. */
function env(name: string, fallback: string): string {
  const value = (import.meta.env as Record<string, string | undefined>)[name];
  return value && value.length > 0 ? value : fallback;
}

function socketUrl(path: string): string {
  const scheme = window.location.protocol === "https:" ? "wss:" : "ws:";
  return `${scheme}//${window.location.host}${path}`;
}

export const API_BASE = "/api";
export const MONITOR_WS = env("VITE_MONITOR_WS", socketUrl("/ws"));
export const LINE_URL = env("VITE_LINE_URL", window.location.origin);
export const LIVE_URL = env("VITE_LIVE_URL", window.location.origin);

export const HOPS_WS = socketUrl("/ws/hops");
export const LINE_WS = socketUrl("/line/ws");

/** The address MetalLB holds for the ingress Service, drawn on the map. */
export const LOAD_BALANCER_ADDRESS = "<INGRESS_IP>";
export const LINE_RATE_MIN = 0;
export const LINE_RATE_MAX = 35;
export const LINE_RATE_STEP = 0.2;
export const LINE_RATE_DEFAULT = 1;

/** Used when /me reports all farms as a wildcard rather than a list. */
export const KNOWN_FARMS = ["farm-a", "farm-b", "farm-c"];

/** The maximum GET /batches accepts. */
export const BATCH_LIST_LIMIT = 200;
export const LOG_LIMIT = 200;
/** Past one ingress read timeout. The ingress may retry a hung GET beyond this, so such a read ends here first. */
export const REQUEST_LIMIT_MS = 65_000;
/** Entries of every level the line keeps in memory. */
export const LOG_CAPACITY = 5_000;
export const FAIL_TARGET_LIMIT = 50;

/** Dispatcher re-sends after 120 s, so an event older than that is no longer normal. */
export const DELAYED_EVENT_MS = 120_000;
/** Monitor reports every 30 s; three missed cycles means the open socket is not delivering. */
export const MONITOR_STALE_MS = 90_000;
/** Frames can arrive hundreds a second under load; the screen redraws four times a second at most. */
export const LIVE_PUBLISH_MS = 250;
