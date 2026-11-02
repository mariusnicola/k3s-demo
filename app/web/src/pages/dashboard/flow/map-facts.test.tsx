import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ConnectionStatus } from "../../../ds";
import { emptySnapshot } from "../../../live/hopFlowRefs";
import type { HopFlow } from "../../../live/useHopFlow";
import { FlowCounts, FlowNotices, MapFacts } from "./FlowNotes";

const flow = (over: Partial<HopFlow["snapshot"]>, realtime = false) => ({ snapshot: { ...emptySnapshot(), ...over }, realtime, speed: 1, follow: null }) as unknown as HopFlow;

describe("REQ-BT-028 what the map says is what it means", () => {
  it("REQ-BT-028 what must outlast a dot stays on the map", () => {
    render(<MapFacts blocked={3} pause={{ paused: true, reason: "fail-next", pod: "w", at: "t" }} />);
    expect(screen.getByText("Blocked: 3. Worker paused: fail-next")).toBeInTheDocument();
  });

  it("REQ-BT-028 no blocked batch and no pause still hold the line, so appearing moves nothing", () => {
    const { container } = render(<MapFacts blocked={0} pause={{ paused: false, reason: "", pod: "w", at: "t" }} />);
    expect(container.querySelector(".ds-live-note")).toBeEmptyDOMElement();
  });

  it("REQ-BT-028 playback says how old its dots are", () => {
    render(<FlowNotices flow={flow({ position: Date.parse("2026-11-01T12:00:00"), buffered: 10 })} lost={false} />);
    expect(screen.getByText(/Dots from 12:00:00 PM, pods and health are live/)).toBeInTheDocument();
  });

  it("REQ-BT-028 hops playback skipped are never called lost by the system", () => {
    render(<FlowNotices flow={flow({ position: 1, buffered: 5000, dropped: 3548 })} lost={false} />);
    expect(screen.getByText(/Skipped 3,548 hops: this page keeps only the newest 5,000/)).toBeInTheDocument();
    expect(screen.getByText(/Nothing was lost by the system/)).toBeInTheDocument();
    expect(screen.queryByText(/Buffer full/)).toBeNull();
  });

  it("REQ-BT-028 the count names dots and when it began", () => {
    render(<FlowCounts flow={flow({ total: 12, moving: 3, since: Date.parse("2026-11-01T12:00:00") }, true)} />);
    expect(screen.getByText(/12 hops seen since 12:00:00 PM in this view, 3 dots on screen, one per batch step/)).toBeInTheDocument();
  });

  it("B-090 a live status line still counts the resets it did not show", () => {
    render(<ConnectionStatus label="Hop stream" state={{ status: "open", lastDataAt: 1000, drops: 12 }} now={2000} />);
    expect(screen.getByText("Hop stream: live")).toBeInTheDocument();
    expect(screen.getByText(/12 resets/)).toBeInTheDocument();
  });
});
