import { describe, expect, it } from "vitest";
import { HOPS_WS, LINE_URL, LINE_WS, MONITOR_WS } from "./config";

describe("browser addresses come from the page, never a host address or port", () => {
  it("monitor socket is /ws on this origin", () => {
    expect(MONITOR_WS).toBe(`ws://${window.location.host}/ws`);
  });

  it("line is this origin", () => {
    expect(LINE_URL).toBe(window.location.origin);
  });

  it("REQ-BT-032 hop socket is /ws/hops on this origin, with no token", () => {
    expect(HOPS_WS).toBe(`ws://${window.location.host}/ws/hops`);
  });

  it("REQ-BT-032 line socket is /line/ws on this origin, with no token", () => {
    expect(LINE_WS).toBe(`ws://${window.location.host}/line/ws`);
  });
});
