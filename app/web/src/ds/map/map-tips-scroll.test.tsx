import { fireEvent, render, screen } from "@testing-library/react";
import type { HopName } from "../../api/types";
import { ArchitectureDiagram } from "../index";

const zero = { api: 0, dispatcher: 0, bus: 0, deliver: 0, worker: 0, record: 0, settle: 0 } as Record<HopName, number>;
const unknown = { state: "unknown", text: "No report" } as const;
const health = { api: unknown, cosmos: unknown, dispatcher: unknown, bus: unknown, worker: unknown } as const;

const at = (top: number) => ({ top, left: 50, width: 40, height: 40, right: 90, bottom: top + 40, x: 50, y: top, toJSON: () => ({}) }) as DOMRect;

describe("map tips and scrolling", () => {
  it("REQ-BT-028 a tip follows its part through a scroll, so focus scrolling a part into view does not close it", () => {
    render(<ArchitectureDiagram getFrame={() => []} counts={zero} failures={zero} health={health} summary="Flow" pods={null} store="mongo" />);
    const part = screen.getByRole("button", { name: /^Line/ });
    part.getBoundingClientRect = () => at(400);
    fireEvent.focus(part);
    const before = screen.getByRole("tooltip").style.top;
    part.getBoundingClientRect = () => at(200);
    fireEvent.scroll(window);
    expect(screen.getByRole("tooltip").style.top).not.toBe(before);
  });

  it("REQ-BT-028 a resize places the tip again", () => {
    render(<ArchitectureDiagram getFrame={() => []} counts={zero} failures={zero} health={health} summary="Flow" pods={null} store="mongo" />);
    const part = screen.getByRole("button", { name: /^Line/ });
    part.getBoundingClientRect = () => at(400);
    fireEvent.mouseOver(part);
    const before = screen.getByRole("tooltip").style.top;
    part.getBoundingClientRect = () => at(200);
    fireEvent(window, new Event("resize"));
    expect(screen.getByRole("tooltip").style.top).not.toBe(before);
  });
});
