import type { Hop } from "../../api/types";

/** Past this a send counts as a delivery the page missed, so a socket reset cannot leave a false wait on screen. */
export const WAIT_KEPT_MS = 5 * 60_000;

export interface WaitView {
  count: number;
  oldestMs: number | null;
}

const leftTheQueue = (hop: Hop) => hop.hop === "deliver" || hop.hop === "worker" || (hop.hop === "dispatcher" && !hop.ok);

/** Steps sent to Service Bus and not delivered yet, read from the hops. Only hops this page saw can count. */
export class QueueWait {
  private sent = new Map<string, number>();
  /** The Dispatcher reports a send after the queue took it, and each pod reports on its own, so the Worker's hop can reach the page first. */
  private left = new Map<string, number>();

  note(hops: readonly Hop[]): void {
    for (const hop of hops) {
      if (hop.hop === "bus" && hop.ok) {
        if (!this.left.has(hop.traceId)) this.sent.set(hop.traceId, Date.parse(hop.at));
      } else if (leftTheQueue(hop)) {
        this.sent.delete(hop.traceId);
        this.left.set(hop.traceId, Date.parse(hop.at));
      }
    }
  }

  clear(): void {
    this.sent.clear();
    this.left.clear();
  }

  view(now: number): WaitView {
    for (const [trace, at] of this.left) if (now - at > WAIT_KEPT_MS) this.left.delete(trace);
    let oldest: number | null = null;
    for (const [trace, at] of this.sent) {
      if (now - at > WAIT_KEPT_MS) this.sent.delete(trace);
      else oldest = oldest === null ? at : Math.min(oldest, at);
    }
    return { count: this.sent.size, oldestMs: oldest === null ? null : now - oldest };
  }
}
