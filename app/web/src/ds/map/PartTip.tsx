import { useLayoutEffect, useRef, useState, type MouseEventHandler } from "react";
import { createPortal } from "react-dom";
import type { PartInfo } from "../../logic/terrain/partInfo";
import { placeTip } from "../../logic/terrain/tipPlace";

export interface PartTipProps {
  id: string;
  info: PartInfo;
  /** One line from the report. Left out when the report holds nothing for this part. */
  live?: string | null;
  /** The part on screen. Without it the tip stands in place, as in the catalog. */
  anchor?: DOMRect | null;
  onMouseEnter?: MouseEventHandler;
  onMouseLeave?: MouseEventHandler;
}

function Words({ info, live }: Pick<PartTipProps, "info" | "live">) {
  return (
    <>
      <p className="ds-part-tip-name">{info.name}</p>
      <p>{info.what}</p>
      <p>{info.does}</p>
      {live ? (
        <p className="ds-part-tip-live" data-live>
          {live}
        </p>
      ) : null}
    </>
  );
}

/** On the page body, so no edge of the map can cut it, and measured there so no edge of the screen can. */
function Floating({ id, info, live, anchor, ...hover }: PartTipProps & { anchor: DOMRect }) {
  const tip = useRef<HTMLDivElement>(null);
  const [place, setPlace] = useState<{ left: number; top: number } | null>(null);
  useLayoutEffect(() => {
    const view = { width: document.documentElement.clientWidth, height: window.innerHeight };
    const { width, height } = tip.current!.getBoundingClientRect();
    setPlace(placeTip(anchor, { width, height }, view));
  }, [anchor, info, live]);
  return createPortal(
    <div ref={tip} id={id} role="tooltip" className="ds-part-tip" data-floating data-placed={place ? "" : undefined} style={place ?? undefined} {...hover}>
      <Words info={info} live={live} />
    </div>,
    document.body,
  );
}

export function PartTip({ anchor, ...rest }: PartTipProps) {
  if (anchor) return <Floating anchor={anchor} {...rest} />;
  return (
    <div id={rest.id} role="tooltip" className="ds-part-tip">
      <Words info={rest.info} live={rest.live} />
    </div>
  );
}
