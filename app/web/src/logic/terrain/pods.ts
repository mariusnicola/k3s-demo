import type { PodReport } from "../../api/monitorTypes";

export type PodService = "api" | "dispatcher" | "worker" | "live" | "ingress" | "metallb" | "other";
export type PodState = "ready" | "not-ready" | "not-reported" | "pending" | "failed" | "unknown" | "terminating";

export const SERVICE_LABEL: Record<PodService, string> = { api: "API", dispatcher: "Dispatcher", worker: "Worker", live: "Live", ingress: "Ingress", metallb: "MetalLB", other: "Other pods" };
export const SERVICES: PodService[] = ["metallb", "ingress", "api", "dispatcher", "worker", "live", "other"];

/** A pod that is not serving stands lower, so state reads without colour. */
export const POD_HEIGHT: Record<PodState, number> = { ready: 0.9, "not-reported": 0.9, "not-ready": 0.55, unknown: 0.55, terminating: 0.35, pending: 0.15, failed: 0.15 };

export const POD_STATE_TEXT: Record<PodState, string> = {
  ready: "Ready",
  "not-ready": "Not ready",
  "not-reported": "Readiness not reported",
  pending: "Pending",
  failed: "Failed",
  unknown: "Unknown",
  terminating: "Terminating",
};

export const POD_PROBLEM: Partial<Record<PodState, string>> = {
  "not-ready": "is not ready",
  pending: "is pending",
  failed: "has failed",
  unknown: "is in an unknown state",
  terminating: "is terminating",
};

export function podService(name: string): PodService {
  if (name.startsWith("ingress-nginx")) return "ingress";
  if (name.startsWith("metallb-")) return "metallb";
  return (/^(api|dispatcher|worker|live)(?:-|$)/.exec(name)?.[1] as PodService | undefined) ?? "other";
}

export function podState(pod: PodReport): PodState {
  if (pod.terminating) return "terminating";
  if (pod.phase === "Failed") return "failed";
  if (pod.phase === "Unknown") return "unknown";
  if (pod.phase === "Pending") return "pending";
  if (pod.ready === undefined) return "not-reported";
  return pod.ready ? "ready" : "not-ready";
}

/** The last part tells replicas of one service apart; other pods are told apart by the service itself. */
export function podLabel(name: string, service: PodService): string {
  const parts = name.split("-");
  return service === "other" ? (parts[0] ?? name) : (parts.at(-1) ?? name).slice(0, 5);
}

export function countNote(count: number): string {
  return `${count} ${count === 1 ? "pod" : "pods"}`;
}
