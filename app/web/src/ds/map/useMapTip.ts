import { useCallback, useEffect, useRef, useState, type FocusEvent, type KeyboardEvent, type MouseEvent } from "react";

/** Long enough to cross the gap from a part to its tip. */
const LEAVE_DELAY = 120;

interface Open {
  part: string;
  element: Element;
  anchor: DOMRect;
  /** Opened by a press, so only a press or leaving it closes it. */
  held: boolean;
}

const partOf = (target: EventTarget) => (target as Element).closest?.("[data-part]") ?? null;

/** While open: Escape or a press outside closes it. A scroll or a resize moves it with its part, because focusing a part scrolls it into view. */
function useOpenWatch(open: Open | null, close: () => void, follow: () => void) {
  useEffect(() => {
    if (!open) return;
    const key = (event: globalThis.KeyboardEvent) => {
      if (event.key === "Escape") close();
    };
    const down = (event: PointerEvent) => {
      if (!partOf(event.target!)) close();
    };
    document.addEventListener("keydown", key);
    document.addEventListener("pointerdown", down);
    window.addEventListener("scroll", follow, true);
    window.addEventListener("resize", follow);
    return () => {
      document.removeEventListener("keydown", key);
      document.removeEventListener("pointerdown", down);
      window.removeEventListener("scroll", follow, true);
      window.removeEventListener("resize", follow);
    };
  }, [open, close, follow]);
}

interface Moves {
  hover: (part: Element) => void;
  leave: () => void;
  focus: (part: Element) => void;
  blur: () => void;
  press: (part: Element) => void;
}

function pictureHandlers({ hover, leave, focus, blur, press }: Moves) {
  return {
    onMouseOver: (event: MouseEvent) => {
      const part = partOf(event.target);
      if (part) hover(part);
    },
    onMouseOut: (event: MouseEvent) => {
      if (partOf(event.target)) leave();
    },
    onFocus: (event: FocusEvent) => {
      const part = partOf(event.target);
      if (part) focus(part);
    },
    onBlur: blur,
    onClick: (event: MouseEvent) => {
      const part = partOf(event.target);
      if (part) press(part);
    },
    onKeyDown: (event: KeyboardEvent) => {
      const part = partOf(event.target);
      if (!part || (event.key !== "Enter" && event.key !== " ")) return;
      event.preventDefault();
      press(part);
    },
  };
}

/** One tip for the whole map. Handlers sit on the picture, so the parts stay plain drawing. */
export function useMapTip() {
  const [open, setOpen] = useState<Open | null>(null);
  const leaving = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const stay = () => clearTimeout(leaving.current);
  const close = useCallback(() => {
    clearTimeout(leaving.current);
    setOpen(null);
  }, []);
  const follow = useCallback(() => setOpen((now) => now && { ...now, anchor: now.element.getBoundingClientRect() }), []);
  const leave = () => {
    stay();
    leaving.current = setTimeout(() => setOpen((now) => (now?.held ? now : null)), LEAVE_DELAY);
  };
  const show = (part: Element, held: boolean) => {
    stay();
    setOpen({ part: part.getAttribute("data-part")!, element: part, anchor: part.getBoundingClientRect(), held });
  };
  const press = (part: Element) => {
    if (open?.held && open.part === part.getAttribute("data-part")) close();
    else show(part, true);
  };

  useOpenWatch(open, close, follow);
  useEffect(() => close, [close]);
  const hover = (part: Element) => {
    if (!open?.held) show(part, false);
  };
  const focus = (part: Element) => show(part, false);
  return { open, picture: pictureHandlers({ hover, leave, focus, blur: close, press }), tipHover: { onMouseEnter: stay, onMouseLeave: leave } };
}
