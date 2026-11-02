const GAP = 8;
const MARGIN = 8;

interface Box {
  left: number;
  top: number;
  width: number;
  height: number;
}

type Size = Pick<Box, "width" | "height">;

/** Between two limits; a tip larger than the room starts at the lower one, so its start stays readable. */
const within = (value: number, low: number, high: number) => Math.max(low, Math.min(value, high));

/** Above the part when it fits, below when it does not, and kept on the screen either way. */
export function placeTip(anchor: Box, tip: Size, view: Size): { left: number; top: number } {
  const above = anchor.top - tip.height - GAP;
  const below = anchor.top + anchor.height + GAP;
  const fits = above >= MARGIN || below + tip.height > view.height - MARGIN;
  return {
    left: within(anchor.left + anchor.width / 2 - tip.width / 2, MARGIN, view.width - tip.width - MARGIN),
    top: within(fits ? above : below, MARGIN, view.height - tip.height - MARGIN),
  };
}
