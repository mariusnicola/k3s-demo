import type { Hop } from "../../api/types";

export const DEFAULT_BUFFER_LIMIT = 5_000;
/** Idle stretches are skipped so playback never sits on an empty screen. */
export const MAX_GAP_MS = 2_000;

export const PLAYBACK_SPEEDS = [0.25, 0.5, 1, 2] as const;
export type PlaybackSpeed = (typeof PLAYBACK_SPEEDS)[number];

function hopTime(hop: Hop): number {
  return Date.parse(hop.at);
}

/** Holds recent hops, oldest first, and replays them on a virtual clock. */
export class PlaybackBuffer {
  private items: Hop[] = [];
  private cursor = 0;
  private playhead: number | null = null;
  private lost = 0;

  constructor(private readonly limit: number = DEFAULT_BUFFER_LIMIT) {}

  get size(): number {
    return this.items.length;
  }

  /** Hops playback skipped: it plays slower than they arrive, and the buffer keeps only the newest. */
  get droppedUnplayed(): number {
    return this.lost;
  }

  get remaining(): number {
    return this.items.length - this.cursor;
  }

  get position(): number | null {
    return this.playhead;
  }

  push(hops: readonly Hop[]): void {
    if (hops.length === 0) return;
    for (const hop of hops) this.items.push(hop);
    const excess = this.items.length - this.limit;
    if (excess <= 0) return;
    this.items.splice(0, excess);
    if (this.playhead !== null) this.lost += Math.max(0, excess - this.cursor);
    this.cursor = Math.max(0, this.cursor - excess);
  }

  start(): void {
    this.cursor = 0;
    this.lost = 0;
    const first = this.items[0];
    this.playhead = first ? hopTime(first) : null;
  }

  stop(): void {
    this.playhead = null;
    this.cursor = 0;
    this.lost = 0;
  }

  /** Moves the virtual clock by real elapsed time times speed; returns hops now due. */
  advance(elapsedMs: number, speed: PlaybackSpeed): Hop[] {
    const next = this.items[this.cursor];
    if (!next) return [];
    const nextAt = hopTime(next);
    const start = this.playhead === null || nextAt - this.playhead > MAX_GAP_MS ? nextAt : this.playhead;
    this.playhead = start + elapsedMs * speed;
    const due: Hop[] = [];
    while (this.cursor < this.items.length) {
      const hop = this.items[this.cursor]!;
      if (hopTime(hop) > this.playhead) break;
      due.push(hop);
      this.cursor += 1;
    }
    return due;
  }
}
