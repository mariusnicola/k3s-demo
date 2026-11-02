import { vi } from "vitest";
import { FakeSocket } from "../mocks/harness";
import { LiveSocket, type MinimalSocket, type SocketState } from "./socket";

function setup() {
  FakeSocket.reset();
  const states: SocketState[] = [];
  const data: unknown[] = [];
  const socket = new LiveSocket<unknown>({
    url: "ws://x",
    parse: (raw) => {
      try {
        return JSON.parse(raw);
      } catch {
        return null;
      }
    },
    onData: (d) => data.push(d),
    onState: (s) => states.push(s),
    create: (url) => new FakeSocket(url),
  });
  socket.start();
  return { socket, states, data, last: () => states.at(-1)! };
}

describe("LiveSocket", () => {
  beforeEach(() => void vi.useFakeTimers());
  afterEach(() => void vi.useRealTimers());

  it("REQ-BT-040 a socket stopped while connecting closes when it opens, because closing it sooner logs a browser warning", () => {
    const fake = { onopen: null, onclose: null, onerror: null, onmessage: null, readyState: 0, close: vi.fn() } as unknown as MinimalSocket & { readyState: number };
    const socket = new LiveSocket<unknown>({ url: "ws://x", parse: () => null, onData: () => undefined, onState: () => undefined, create: () => fake });
    socket.start();
    socket.stop();
    expect(fake.close).not.toHaveBeenCalled();
    (fake.onopen as unknown as () => void)();
    expect(fake.close).toHaveBeenCalledTimes(1);
  });

  it("REQ-BT-040 a stopped socket that was open closes at once", () => {
    const fake = { onopen: null, onclose: null, onerror: null, onmessage: null, readyState: 1, close: vi.fn() } as unknown as MinimalSocket;
    const socket = new LiveSocket<unknown>({ url: "ws://x", parse: () => null, onData: () => undefined, onState: () => undefined, create: () => fake });
    socket.start();
    socket.stop();
    expect(fake.close).toHaveBeenCalledTimes(1);
  });

  it("B-046 a socket that outlives the session backs off through repeated 4401s, even when each one opened first", () => {
    vi.useFakeTimers();
    FakeSocket.reset();
    const onUnauthorized = vi.fn();
    new LiveSocket<string>({ url: "/ws", parse: (raw) => raw, onData: () => undefined, onState: () => undefined, onUnauthorized, outlivesSession: true, create: (url) => new FakeSocket(url) }).start();
    const expire = () => {
      FakeSocket.all.at(-1)!.open();
      FakeSocket.all.at(-1)!.drop(4401);
    };
    expire();
    vi.advanceTimersByTime(200);
    expect(FakeSocket.all).toHaveLength(2);
    expire();
    vi.advanceTimersByTime(400);
    expect(FakeSocket.all).toHaveLength(3);
    expire();
    vi.advanceTimersByTime(399);
    expect(FakeSocket.all).toHaveLength(3);
    expect(onUnauthorized).toHaveBeenCalledTimes(3);
    vi.useRealTimers();
  });

  it("REQ-BT-032 close 4401 ends the session and never reconnects", () => {
    FakeSocket.reset();
    const onUnauthorized = vi.fn();
    const socket = new LiveSocket<unknown>({ url: "ws://x", parse: () => null, onData: () => undefined, onState: () => undefined, onUnauthorized, create: (url) => new FakeSocket(url) });
    socket.start();
    FakeSocket.all[0]!.open();
    FakeSocket.all[0]!.drop(4401);
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(120_000);
    expect(FakeSocket.all).toHaveLength(1);
  });

  it("REQ-BT-032 close 1013 reconnects like any other close", () => {
    const t = setup();
    FakeSocket.all[0]!.open();
    FakeSocket.all[0]!.drop(1013);
    vi.advanceTimersByTime(1_000);
    expect(FakeSocket.all).toHaveLength(2);
    t.socket.stop();
  });

  it("REQ-BT-030 a service down at load stays lost across retries", () => {
    const t = setup();
    FakeSocket.all[0]!.drop();
    expect(t.last().status).toBe("lost");
    vi.advanceTimersByTime(1_000);
    expect(t.last().status).toBe("lost");
    expect(t.states.filter((s) => s.status === "connecting").length).toBeLessThanOrEqual(1);
    t.socket.stop();
  });

  it("REQ-BT-030 open then drop reports lost and keeps the last data time", () => {
    const t = setup();
    const raw = FakeSocket.all[0]!;
    raw.open();
    raw.send([1]);
    const lastData = t.last().lastDataAt;
    expect(lastData).not.toBeNull();
    raw.drop();
    expect(t.last()).toEqual({ status: "lost", lastDataAt: lastData, drops: 1, reportId: expect.stringMatching(/^[0-9a-f]{8}-/) });
  });

  it("REQ-BT-030 a retry attempt stays lost, never connecting or open", () => {
    const t = setup();
    const raw = FakeSocket.all[0]!;
    raw.open();
    raw.drop();
    vi.advanceTimersByTime(1000);
    expect(FakeSocket.all).toHaveLength(2);
    expect(t.last().status).toBe("lost");
  });

  it("REQ-BT-030 retries back off up to the ceiling", () => {
    vi.spyOn(Math, "random").mockReturnValue(1);
    setup();
    let count = 1;
    for (let i = 0; i < 8; i += 1) {
      FakeSocket.all.at(-1)!.drop();
      vi.advanceTimersByTime(29_000);
      if (i < 4) expect(FakeSocket.all.length).toBeGreaterThan(count);
      count = FakeSocket.all.length;
      vi.advanceTimersByTime(1000);
    }
    const before = FakeSocket.all.length;
    FakeSocket.all.at(-1)!.drop();
    vi.advanceTimersByTime(29_000);
    expect(FakeSocket.all).toHaveLength(before);
  });

  it("REQ-BT-030 a message that does not parse is not counted as data", () => {
    const t = setup();
    FakeSocket.all[0]!.open();
    FakeSocket.all[0]!.onmessage?.({ data: "{broken" });
    expect(t.last().lastDataAt).toBeNull();
    expect(t.data).toEqual([]);
  });

  it("REQ-BT-030 reconnect now does not wait for the backoff", () => {
    const t = setup();
    FakeSocket.all[0]!.drop();
    t.socket.reconnectNow();
    expect(FakeSocket.all).toHaveLength(2);
  });

  it("REQ-BT-032 stop leaves no timer and no socket", () => {
    const t = setup();
    FakeSocket.all[0]!.drop();
    t.socket.stop();
    vi.advanceTimersByTime(60_000);
    expect(FakeSocket.all).toHaveLength(1);
  });

  it("REQ-BT-030 a constructor throw counts as lost and retries", () => {
    let tries = 0;
    const states: SocketState[] = [];
    const socket = new LiveSocket<unknown>({
      url: "ws://x",
      parse: () => null,
      onData: () => undefined,
      onState: (s) => states.push(s),
      create: () => {
        tries += 1;
        throw new Error("blocked");
      },
    });
    socket.start();
    vi.advanceTimersByTime(1000);
    expect(tries).toBeGreaterThan(1);
    expect(states.at(-1)?.status).toBe("lost");
    socket.stop();
  });
});
