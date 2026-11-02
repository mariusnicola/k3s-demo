import type { HopFlow } from "../../../live/useHopFlow";
import { PLAYBACK_SPEEDS, type PlaybackSpeed } from "../../../logic/flow/playback";
import { Button, Checkbox, Cluster, SegmentedControl } from "../../../ds";

export function speedLabel(speed: PlaybackSpeed): string {
  return `${speed}x`;
}

export function FlowControls({ flow }: { flow: HopFlow }) {
  const { realtime, speed, follow } = flow;
  return (
    <Cluster gap={5} align="end">
      <Checkbox label="RealTime Monitor" checked={realtime} onChange={flow.setRealtime} help={realtime ? "Dots move as hops arrive." : "Dots replay from the buffer."} />
      <SegmentedControl legend="Playback speed" disabled={realtime} help={realtime ? "Turn RealTime Monitor off to choose a speed." : undefined} value={String(speed)} onChange={(v) => flow.setSpeed(Number(v) as PlaybackSpeed)} options={PLAYBACK_SPEEDS.map((s) => ({ value: String(s), label: speedLabel(s) }))} />
      {!realtime ? (
        <Button icon="refresh" onClick={flow.restart}>
          Restart playback
        </Button>
      ) : null}
      {follow ? (
        <Button icon="close" onClick={() => flow.setFollow(null)}>
          Show all traces
        </Button>
      ) : null}
    </Cluster>
  );
}
