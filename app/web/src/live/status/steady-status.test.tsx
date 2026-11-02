import { act, render, screen } from "@testing-library/react";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ConnectionStatus } from "../../ds";
import { FakeSocket, useFakeSockets } from "../../mocks/harness";
import { startBrowserReports } from "../../api/reports";
import { useLiveSocket, setLostShownAfter } from "../useLiveSocket";
import { LOST_SHOWN_AFTER_MS, SteadyStatus } from "./steadyStatus";

const URL = "ws://test/feed";
const parse = (raw: string) => raw;

function Feed() {
  const feed = useLiveSocket(URL, parse, () => undefined);
  return <ConnectionStatus label="Hop stream" state={feed.state} now={Date.now()} onReconnect={feed.reconnect} />;
}

const socket = () => FakeSocket.find(URL);
const pass = (ms: number) => act(() => vi.advanceTimersByTime(ms));
const open = () =>
  act(() => {
    socket().open();
    socket().send("frame");
  });

describe("B-090 a connection warning appears only for a real loss", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    useFakeSockets();
    setLostShownAfter(LOST_SHOWN_AFTER_MS);
  });
  afterEach(() => {
    vi.useRealTimers();
    setLostShownAfter(0);
  });

  it("B-090 a drop and reconnect inside the time shows nothing", () => {
    render(<Feed />);
    open();
    expect(screen.getByText("Hop stream: live")).toBeInTheDocument();
    act(() => socket().drop());
    expect(screen.getByText("Hop stream: live")).toBeInTheDocument();
    pass(1_000);
    open();
    pass(LOST_SHOWN_AFTER_MS * 2);
    expect(screen.getByText("Hop stream: live")).toBeInTheDocument();
    expect(screen.queryByText(/connection lost/)).toBeNull();
  });

  it("B-090 every drop is still reported, shown or not", () => {
    const sender = vi.fn();
    const stop = startBrowserReports(sender);
    render(<Feed />);
    open();
    act(() => socket().drop());
    pass(1_000);
    open();
    expect(sender).toHaveBeenCalledTimes(1);
    expect(sender.mock.calls[0]![0]).toMatchObject({ kind: "socket-failure" });
    stop();
  });

  it("B-090 a loss that lasts shows once, and stays one alert while the socket keeps retrying", () => {
    render(<Feed />);
    open();
    act(() => socket().drop());
    pass(LOST_SHOWN_AFTER_MS - 1);
    expect(screen.queryByText(/connection lost/)).toBeNull();
    pass(1);
    const alert = screen.getByRole("alert");
    expect(alert).toHaveTextContent("Hop stream: connection lost");
    pass(LOST_SHOWN_AFTER_MS * 3);
    expect(screen.getByRole("alert")).toBe(alert);
  });

  it("B-090 repeated drops that each bring data never flicker", () => {
    render(<Feed />);
    open();
    for (let i = 0; i < 20; i += 1) {
      act(() => socket().drop());
      expect(screen.getByText("Hop stream: live")).toBeInTheDocument();
      pass(500);
      open();
      expect(screen.getByText("Hop stream: live")).toBeInTheDocument();
      pass(500);
    }
    expect(screen.queryByText(/connection lost/)).toBeNull();
  });

  it("B-090 a socket that opens and dies with no data is lost, however often it opens", () => {
    render(<Feed />);
    open();
    for (let i = 0; i < 6; i += 1) {
      act(() => socket().drop());
      pass(900);
      act(() => socket().open());
      pass(100);
    }
    expect(screen.getByRole("alert")).toHaveTextContent("Hop stream: connection lost");
  });

  it("B-090 once shown, the warning clears once, on data", () => {
    render(<Feed />);
    open();
    act(() => socket().drop());
    pass(LOST_SHOWN_AFTER_MS);
    expect(screen.getByRole("alert")).toBeInTheDocument();
    pass(30_000);
    open();
    expect(screen.queryByRole("alert")).toBeNull();
    act(() => socket().drop());
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("B-090 a quiet socket that stays open clears it too", () => {
    const shown: string[] = [];
    const steady = new SteadyStatus((s) => shown.push(s.status), LOST_SHOWN_AFTER_MS);
    steady.next({ status: "open", lastDataAt: null });
    steady.next({ status: "lost", lastDataAt: null });
    vi.advanceTimersByTime(LOST_SHOWN_AFTER_MS);
    steady.next({ status: "open", lastDataAt: null });
    expect(shown.at(-1)).toBe("lost");
    vi.advanceTimersByTime(LOST_SHOWN_AFTER_MS);
    expect(shown.at(-1)).toBe("open");
    steady.stop();
  });

  it("B-090 a first connection that never opens is lost at once, because nothing was shown live", () => {
    const shown: string[] = [];
    const steady = new SteadyStatus((s) => shown.push(s.status), LOST_SHOWN_AFTER_MS);
    steady.next({ status: "connecting", lastDataAt: null });
    steady.next({ status: "lost", lastDataAt: null });
    vi.advanceTimersByTime(LOST_SHOWN_AFTER_MS);
    expect(shown.at(-1)).toBe("lost");
    steady.stop();
  });
});

describe("B-090 a connection state takes no room in the page", () => {
  const css = readFileSync(resolve(__dirname, "../../ds/feedback/connection.css"), "utf8");

  it("B-090 the fault detail is an overlay, and the status line holds one height", () => {
    render(<ConnectionStatus label="Hop stream" state={{ status: "lost", lastDataAt: 0, reportId: "r-1" }} now={1000} onReconnect={() => undefined} />);
    const more = document.querySelector(".ds-connection-more")!;
    expect(more).toHaveTextContent("Retrying.");
    expect(more).toHaveTextContent("Report ID");
    expect(more).toHaveTextContent("Reconnect now");
    expect(css).toMatch(/\.ds-connection-more \{[^}]*position: absolute/);
    expect(css).toMatch(/\.ds-connection \{[^}]*position: relative/);
    expect(css).toMatch(/\.ds-connection \{[^}]*min-height: /);
    expect(css).toMatch(/\.ds-connection \{[^}]*min-width: /);
  });

  it("B-090 going lost mounts the floating box new, because a box that morphs from the inline detail counts as a shift", () => {
    const line = (status: "open" | "lost") => <ConnectionStatus label="Hop stream" state={{ status, lastDataAt: 0 }} now={1000} onReconnect={() => undefined} />;
    const { rerender } = render(line("open"));
    const detail = document.querySelector(".ds-connection-detail")!;
    rerender(line("lost"));
    expect(document.querySelector(".ds-connection-more")).not.toBe(detail);
    expect(detail.isConnected).toBe(false);
  });

  it("B-090 the lost and the live status line use the same box", () => {
    expect(css).not.toMatch(/\[data-status="lost"\] \{[^}]*padding/);
  });
});
