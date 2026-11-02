import { placeTip } from "./tipPlace";

const view = { width: 1000, height: 800 };
const size = { width: 300, height: 120 };
const anchor = (left: number, top: number, width = 40, height = 40) => ({ left, top, width, height });

describe("placeTip", () => {
  it("REQ-BT-028 sits above the part, centred on it", () => {
    expect(placeTip(anchor(480, 400), size, view)).toEqual({ left: 350, top: 272 });
  });

  it("REQ-BT-028 drops below the part when there is no room above", () => {
    expect(placeTip(anchor(480, 60), size, view)).toEqual({ left: 350, top: 108 });
  });

  it("REQ-BT-028 never passes the left or the right edge", () => {
    expect(placeTip(anchor(0, 400), size, view).left).toBe(8);
    expect(placeTip(anchor(960, 400), size, view).left).toBe(692);
  });

  it("REQ-BT-028 with room on neither side it stays on the screen", () => {
    const small = { width: 320, height: 200 };
    const tall = { width: 300, height: 180 };
    const place = placeTip(anchor(100, 90, 40, 20), tall, small);
    expect(place.top).toBeGreaterThanOrEqual(8);
    expect(place.top + tall.height).toBeLessThanOrEqual(192);
  });

  it("REQ-BT-028 a tip wider than the screen starts at the margin", () => {
    expect(placeTip(anchor(10, 400), { width: 400, height: 100 }, { width: 320, height: 800 }).left).toBe(8);
  });
});
