import { backoffMs } from "../logic/backoff";
import { reportFailure } from "../api/reports";

export type SocketStatus = "connecting" | "open" | "lost";

export interface SocketState {
  status: SocketStatus;
  /** Time of the last message that parsed, in ms since the epoch. */
  lastDataAt: number | null;
  /** Set while lost, for the page to show. */
  reportId?: string | null;
  /** Drops since this page opened, so a short loss the status line never shows is still counted. */
  drops?: number;
}

export interface MinimalSocket {
  onopen: ((event: never) => void) | null;
  onclose: ((event: { code: number }) => void) | null;
  onerror: ((event: never) => void) | null;
  onmessage: ((event: { data: unknown }) => void) | null;
  close(): void;
  readyState?: number;
}

export interface LiveSocketOptions<T> {
  url: string;
  parse: (raw: string) => T | null;
  onData: (data: T) => void;
  onState: (state: SocketState) => void;
  /** The server closed with 4401: the session is over, so retrying would only be refused. */
  onUnauthorized?: () => void;
  /** For a feed with something to show after a session too: a 4401 retries, with backoff, instead of stopping. */
  outlivesSession?: boolean;
  create?: (url: string) => MinimalSocket;
}

export const CLOSE_UNAUTHORIZED = 4401;
const CONNECTING = 0;

/** WebSocket that keeps trying and always reports what it knows. Retries back off so an outage stays cheap. */
export class LiveSocket<T> {
  private socket: MinimalSocket | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private failures = 0;
  /** Kept apart from failures, which an open resets: a socket that opens and is refused each time must still slow down. */
  private refused = 0;
  private stopped = true;
  private opened = false;
  private state: SocketState = { status: "connecting", lastDataAt: null };

  constructor(private readonly options: LiveSocketOptions<T>) {}

  start(): void {
    this.stopped = false;
    this.connect();
  }

  stop(): void {
    this.stopped = true;
    this.clearTimer();
    this.detach();
  }

  reconnectNow(): void {
    if (this.stopped) return;
    this.clearTimer();
    this.detach();
    this.failures = 0;
    this.connect();
  }

  private connect(): void {
    // A retry after a failure stays lost; flipping to connecting would hide Reconnect and repeat the alert.
    this.update({ status: this.opened || this.state.status === "lost" ? "lost" : "connecting" });
    const create = this.options.create ?? ((url: string) => new WebSocket(url) as unknown as MinimalSocket);
    let socket: MinimalSocket;
    try {
      socket = create(this.options.url);
    } catch {
      this.noteLost("Socket could not be created");
      this.scheduleRetry();
      return;
    }
    this.socket = socket;
    this.attach(socket);
  }

  private attach(socket: MinimalSocket): void {
    socket.onopen = () => {
      this.failures = 0;
      this.opened = true;
      this.update({ status: "open" });
    };
    socket.onmessage = (event) => {
      if (typeof event.data !== "string") return;
      const data = this.options.parse(event.data);
      if (data === null) return;
      this.update({ lastDataAt: Date.now() });
      this.options.onData(data);
    };
    socket.onerror = () => undefined;
    socket.onclose = (event) => {
      this.detach();
      this.update({ status: "lost", drops: (this.state.drops ?? 0) + 1 });
      // A 4401 is the session ending, which the page says in its own words.
      if (event.code !== CLOSE_UNAUTHORIZED) this.noteLost(`Socket closed with code ${event.code}`);
      if (event.code === CLOSE_UNAUTHORIZED) {
        this.stopped = !this.options.outlivesSession;
        this.options.onUnauthorized?.();
        this.failures = this.refused++;
      }
      this.scheduleRetry();
    };
  }

  private noteLost(what: string): void {
    const path = this.options.url.replace(/^\w+:\/\/[^/]*/, "");
    this.update({ reportId: reportFailure({ kind: "socket-failure", status: null, correlationId: null, message: `${what}: ${path}` }) });
  }

  private scheduleRetry(): void {
    if (this.stopped) return;
    this.update({ status: "lost" });
    this.failures += 1;
    this.timer = setTimeout(() => this.connect(), backoffMs(this.failures));
  }

  private detach(): void {
    const socket = this.socket;
    this.socket = null;
    if (!socket) return;
    socket.onclose = socket.onerror = socket.onmessage = null;
    // Closing before the open logs a browser warning, which the console rule fails on.
    if (socket.readyState === CONNECTING) {
      socket.onopen = () => socket.close();
      return;
    }
    socket.onopen = null;
    socket.close();
  }

  private clearTimer(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }

  private update(patch: Partial<SocketState>): void {
    const next = { ...this.state, ...patch };
    if (next.status === this.state.status && next.lastDataAt === this.state.lastDataAt && next.reportId === this.state.reportId) return;
    this.state = next;
    this.options.onState(next);
  }
}
