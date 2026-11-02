import { act, renderHook } from "@testing-library/react";
import { useEffect } from "react";
import { beforeEach, describe, expect, it } from "vitest";
import { FakeSocket, useFakeSockets } from "../mocks/harness";
import { useLiveSocket } from "./useLiveSocket";

const parse = (raw: string) => raw;
const onData = () => undefined;

beforeEach(useFakeSockets);

describe("B-011 sign out and the socket state", () => {
  function signedIn(seen: string[]) {
    const view = renderHook(
      ({ url }: { url: string | null }) => {
        const feed = useLiveSocket(url, parse, onData);
        useEffect(() => void seen.push(feed.state.status));
        return feed;
      },
      { initialProps: { url: "/ws/x" as string | null } },
    );
    act(() => FakeSocket.find("/ws/x").open());
    act(() => FakeSocket.find("/ws/x").send("frame"));
    return view;
  }

  it("B-011 signing out clears status and last data", () => {
    const view = signedIn([]);
    expect(view.result.current.state.status).toBe("open");
    view.rerender({ url: null });
    expect(view.result.current.state).toEqual({ status: "connecting", lastDataAt: null });
  });

  it("B-011 the next sign in never shows the old open, not for one render", () => {
    const seen: string[] = [];
    const view = signedIn(seen);
    view.rerender({ url: null });
    seen.length = 0;
    view.rerender({ url: "/ws/x" });
    expect(seen).not.toContain("open");
  });

  it("B-011 Reconnect now does nothing to a stopped socket", () => {
    const view = signedIn([]);
    const old = FakeSocket.find("/ws/x");
    view.rerender({ url: null });
    act(() => view.result.current.reconnect());
    expect(old.closed).toBe(true);
    expect(() => FakeSocket.find("/ws/x")).toThrow();
  });
});
