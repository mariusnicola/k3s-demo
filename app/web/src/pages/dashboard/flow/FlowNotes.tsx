import type { HopFlow } from "../../../live/useHopFlow";
import { counted, formatCount, formatTime, shortId } from "../../../logic/format";
import type { WorkerPause } from "../../../api/types";
import { Banner, LiveNote, Text } from "../../../ds";
import { speedLabel } from "./FlowControls";

export function FlowNotices({ flow, lost }: { flow: HopFlow; lost: boolean }) {
  const { snapshot, realtime, speed } = flow;
  return (
    <>
      {lost ? (
        <Banner floating="panel" tone="warning" title={realtime ? "Dots stopped: the hop stream is lost" : "Playing the buffer only: the hop stream is lost"} live="off">
          Hops created while disconnected are not shown. New dots return with the stream.
        </Banner>
      ) : null}
      {!realtime ? (
        <Text size="xs" tone="secondary" as="p">
          {snapshot.position !== null ? `Dots from ${formatTime(snapshot.position)}, pods and health are live. ` : ""}
          {snapshot.buffered === 0
            ? "Playback: no hops buffered yet. It starts when the first hop arrives."
            : `Playback at ${speedLabel(speed)}: position ${formatTime(snapshot.position)}, ${counted(snapshot.behind, "hop")} still to play, ${formatCount(snapshot.buffered)} in the buffer.`}
          {snapshot.dropped > 0 ? ` Skipped ${counted(snapshot.dropped, "hop")}: this page keeps only the newest ${formatCount(snapshot.buffered)}, and they arrive faster than playback runs. Nothing was lost by the system.` : ""}
        </Text>
      ) : null}
    </>
  );
}

/** What must outlast a dot. It holds a reserved line, so it appearing moves nothing. */
export function MapFacts({ blocked, pause }: { blocked: number | null; pause: WorkerPause | null }) {
  const facts = [blocked ? `Blocked: ${formatCount(blocked)}` : null, pause?.paused ? `Worker paused: ${pause.reason}` : null].filter(Boolean);
  return <LiveNote>{facts.join(". ")}</LiveNote>;
}

export function FlowCounts({ flow }: { flow: HopFlow }) {
  const { snapshot, follow } = flow;
  return (
    <Text size="xs" tone="secondary" as="p">
      {follow ? `Following trace ${shortId(follow)}. Hop and station counts still cover all traces. ` : ""}
      {counted(snapshot.total, "hop")} seen{snapshot.since === null ? "" : ` since ${formatTime(snapshot.since)}`} in this view, {counted(snapshot.moving, "dot")} on screen, one per batch step
      {snapshot.undrawn > 0 ? `, ${formatCount(snapshot.undrawn)} more steps not drawn` : ""}. A failed hop draws a diamond instead of a dot.
    </Text>
  );
}
