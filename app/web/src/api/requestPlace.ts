import type { ProblemRequest } from "./problem";

export interface RequestPlace {
  where: string;
  batch: boolean;
  /** The address names one batch, unlike the list and create. */
  one?: boolean;
}

const BATCH_ACTIONS: Record<string, string> = {
  advance: "Moving batch",
  inspect: "Recording the inspection of batch",
  "fail-next": "Setting up a failed delivery for batch",
  replay: "Replaying the event of batch",
};

export function requestPlace({ method, url }: ProblemRequest): RequestPlace | null {
  const path = url.replace(/^[a-z]+:\/\/[^/]+/i, "").split("?")[0]!;
  const parts = path.split("/").filter(Boolean).map(decodeURIComponent);
  if (path === "/api/batches") return { where: method === "GET" ? "Reading the batch list" : "Creating a batch", batch: true };
  if (parts[0] === "api" && parts[1] === "batches" && parts.length >= 4) {
    const named = `${parts[3]} on ${parts[2]}`;
    const action = parts[4];
    if (!action) return { where: `Reading batch ${named}`, batch: true, one: true };
    return BATCH_ACTIONS[action] ? { where: `${BATCH_ACTIONS[action]} ${named}`, batch: true, one: true } : null;
  }
  if (path === "/api/compliance/run") return { where: "Running the nightly compliance check", batch: false };
  if (path === "/api/auth/dev-login") return { where: "Signing in", batch: false };
  if (path === "/api/auth/logout") return { where: "Signing out", batch: false };
  if (path === "/api/me" || path === "/api/session") return { where: "Reading your session", batch: false };
  if (path === "/line/logs") return { where: "Reading the logs", batch: false };
  if (path === "/live/logs") return { where: "Reading the service logs", batch: false };
  if (path === "/line") return { where: method === "DELETE" ? "Stopping the production line" : "Starting the line or changing its rate", batch: false };
  if (path === "/metrics/series") return { where: "Reading performance data", batch: false };
  return null;
}
