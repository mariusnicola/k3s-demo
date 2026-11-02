import { LINE_WS } from "../config";
import { parseLineFrame, type LineFrame } from "../logic/feed/live";
import { useLiveSocket, type Feed } from "./useLiveSocket";

export function useLineFeed(onFrame: (frame: LineFrame) => void): Feed {
  return useLiveSocket(LINE_WS, parseLineFrame, onFrame);
}
