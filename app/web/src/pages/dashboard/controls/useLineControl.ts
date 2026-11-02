import { useEffect, useRef, useState } from "react";
import { client } from "../../../api/client";
import { LINE_RATE_DEFAULT } from "../../../config";
import { useAction } from "../../../live/useAction";
import { useLineFeed } from "../../../live/useLineFeed";
import { type LineFrame } from "../../../logic/feed/live";
import { achievedRate, keepSamples, type RateSample } from "../../../logic/monitor/lineRate";

/** With the socket lost, an accepted command has no other sign on screen. Any frame carries the truth and ends this. */
function useLineCommands(action: ReturnType<typeof useAction>, frame: LineFrame | null) {
  const [stopping, setStopping] = useState(false);
  const [sent, setSent] = useState<"start" | "stop" | null>(null);
  useEffect(() => setSent(null), [frame]);
  const start = (rate: number) => {
    const accepted = async () => {
      await client.lineStart(rate);
      setSent("start");
    };
    return action.run(accepted);
  };
  const stop = () => {
    setStopping(true);
    const accepted = async () => {
      await client.lineStop();
      setSent("stop");
    };
    void action.run(accepted).then(() => setStopping(false));
  };
  return { stopping, sent, start, stop };
}

/** The rate field is what was asked for; this is what the Line's own counter did. */
function useAchievedRate(frame: LineFrame | null): number | null {
  const samples = useRef<RateSample[]>([]);
  if (frame && samples.current.at(-1)?.at !== frame.at) samples.current = keepSamples(samples.current, { at: frame.at, started: frame.status.started });
  return achievedRate(samples.current);
}

/** One call at a time, latest value only: overlapping calls could land in the wrong order. */
function useRateSender(start: (rate: number) => Promise<boolean>, finished: (accepted: boolean) => void) {
  const waiting = useRef<number | null>(null);
  const sending = useRef(false);
  const drain = async () => {
    sending.current = true;
    while (waiting.current !== null) {
      const value = waiting.current;
      waiting.current = null;
      const accepted = await start(value);
      if (waiting.current === null) finished(accepted);
    }
    sending.current = false;
  };
  return (value: number) => {
    waiting.current = value;
    if (!sending.current) void drain();
  };
}

/**
 * Null until moved, so the lever shows the rate the line really runs at. After an accepted
 * change it waits for a frame before letting go, or it would flick back to the old rate.
 */
function useRateLever(start: (rate: number) => Promise<boolean>, frame: LineFrame | null) {
  const [edited, setEdited] = useState<number | null>(null);
  const [rateFailed, setRateFailed] = useState(false);
  const settling = useRef(false);
  useEffect(() => {
    if (settling.current) setEdited(null);
    settling.current = false;
  }, [frame]);

  const moveRate = (value: number) => {
    settling.current = false;
    setRateFailed(false);
    setEdited(value);
  };
  const settle = (accepted: boolean) => {
    settling.current = accepted;
  };
  const sendRate = useRateSender(start, (accepted) => {
    settle(accepted);
    if (!accepted) setEdited(null);
    setRateFailed(!accepted);
  });
  return { edited, rateFailed, moveRate, settle, sendRate };
}

export function useLineControl() {
  const [frame, setFrame] = useState<LineFrame | null>(null);
  const feed = useLineFeed(setFrame);
  const status = frame?.status;
  const lost = feed.state.status === "lost";
  const achieved = useAchievedRate(frame);
  const action = useAction();
  const { stopping, sent, start, stop } = useLineCommands(action, frame);
  const running = lost && sent ? sent === "start" : (status?.running ?? false);
  const producing = running && (status?.rate ?? 0) > 0;
  const { edited, rateFailed, moveRate, settle, sendRate } = useRateLever(start, frame);
  const rate = edited ?? (running && status ? status.rate : LINE_RATE_DEFAULT);

  const submit = () => void start(rate).then(settle);
  // POST /line also starts a stopped line, so a stopped lever only holds its value for Start line.
  const releaseRate = (value: number) => {
    if (running) sendRate(value);
  };
  return { achieved, frame, feed, status, lost, sent, moveRate, releaseRate, rateFailed, action, running, producing, stopping, rate, submit, stop };
}

export type LineControl = ReturnType<typeof useLineControl>;
