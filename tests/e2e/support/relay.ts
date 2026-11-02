import net from "node:net";
import { BASE } from "./env";

/**
 * Plain TCP pass-through between Chromium and the ingress. Browser offline mode leaves an open
 * WebSocket alive, so a real socket loss needs a real cut. Every byte is the server's own.
 */
export class Relay {
  private readonly server: net.Server;
  private readonly upstream = new URL(BASE);
  private readonly sockets = new Set<net.Socket>();
  private readonly connections = new Set<{ client: net.Socket; isSocket: boolean }>();
  private blockSockets = false;
  private blockAll = false;

  constructor() {
    this.server = net.createServer((client) => this.accept(client));
  }

  async start(): Promise<void> {
    await new Promise<void>((resolve) => this.server.listen(0, "127.0.0.1", resolve));
  }

  /** Chromium flag that sends only the app origin through this relay. */
  get hostRule(): string {
    const port = (this.server.address() as net.AddressInfo).port;
    return `--host-resolver-rules=MAP ${this.upstream.hostname}:${this.upstream.port || 80} 127.0.0.1:${port}`;
  }

  /** Cuts open WebSockets and refuses new ones. Plain requests still pass. */
  dropSockets(): void {
    this.blockSockets = true;
    for (const c of this.connections) if (c.isSocket) c.client.destroy();
  }

  /** Cuts everything, as a pulled cable does. */
  cutAll(): void {
    this.blockAll = true;
    for (const socket of this.sockets) socket.destroy();
  }

  restore(): void {
    this.blockSockets = false;
    this.blockAll = false;
  }

  async stop(): Promise<void> {
    for (const socket of this.sockets) socket.destroy();
    await new Promise<void>((resolve) => this.server.close(() => resolve()));
  }

  private accept(client: net.Socket): void {
    if (this.blockAll) {
      client.destroy();
      return;
    }
    const upstream = net.connect(Number(this.upstream.port || 80), this.upstream.hostname);
    const entry = { client, isSocket: false };
    this.sockets.add(client);
    this.sockets.add(upstream);
    this.connections.add(entry);
    const close = () => {
      client.destroy();
      upstream.destroy();
      this.sockets.delete(client);
      this.sockets.delete(upstream);
      this.connections.delete(entry);
    };
    client.on("error", close).on("close", close);
    upstream.on("error", close).on("close", close);
    client.once("data", (first) => {
      entry.isSocket = /^upgrade:\s*websocket/im.test(first.toString("latin1"));
      if (entry.isSocket && this.blockSockets) {
        close();
        return;
      }
      upstream.write(first);
      client.pipe(upstream);
    });
    upstream.pipe(client);
  }
}
