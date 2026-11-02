import type { SocketState } from "../socket";

/** Long enough for four quick retries with their backoff. A shorter drop heals itself, so showing it only makes the page flicker. */
export const LOST_SHOWN_AFTER_MS = 5_000;

/** What the page shows of a socket. Reports are made in the socket, on every drop, so this changes only what is drawn. */
export class SteadyStatus {
  private latest: SocketState = { status: "connecting", lastDataAt: null };
  private shown: SocketState = this.latest;
  private lostTimer: ReturnType<typeof setTimeout> | null = null;
  private settleTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private readonly show: (state: SocketState) => void,
    private readonly holdMs: number,
  ) {}

  next(state: SocketState): void {
    const data = state.lastDataAt !== this.latest.lastDataAt;
    this.latest = state;
    if (this.holdMs <= 0 || this.shown.status === "connecting" || (state.status === "open" && data)) this.settled();
    else if (state.status === "lost") this.onLost();
    else this.onReturned();
  }

  stop(): void {
    this.clearTimers();
  }

  private onLost(): void {
    this.clearSettle();
    if (this.shown.status !== "lost") this.lostTimer ??= setTimeout(() => this.reveal(), this.holdMs);
    this.draw(this.shown.status);
  }

  /** Open with no data proves nothing yet, so it counts only after it has lasted. */
  private onReturned(): void {
    if (this.lostTimer === null && this.shown.status !== "lost") {
      this.settled();
      return;
    }
    this.settleTimer ??= setTimeout(() => this.settled(), this.holdMs);
    this.draw(this.shown.status);
  }

  private reveal(): void {
    this.lostTimer = null;
    this.draw("lost");
  }

  private settled(): void {
    this.clearTimers();
    this.draw(this.latest.status);
  }

  private draw(status: SocketState["status"]): void {
    this.shown = { ...this.latest, status };
    this.show(this.shown);
  }

  private clearSettle(): void {
    if (this.settleTimer !== null) clearTimeout(this.settleTimer);
    this.settleTimer = null;
  }

  private clearTimers(): void {
    this.clearSettle();
    if (this.lostTimer !== null) clearTimeout(this.lostTimer);
    this.lostTimer = null;
  }
}
