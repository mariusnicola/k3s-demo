import type { BatchState } from "../../api/types";

export interface BatchQuery {
  state?: BatchState;
  farmId?: string;
  limit?: number;
}

export function batchQuery(query: BatchQuery): string {
  const params = new URLSearchParams();
  if (query.state) params.set("state", query.state);
  if (query.farmId) params.set("farmId", query.farmId);
  if (query.limit) params.set("limit", String(query.limit));
  const text = params.toString();
  return text ? `?${text}` : "";
}
