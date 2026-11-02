export interface Throttled {
  (): void;
  cancel(): void;
}

/** Runs at once, then at most once per window, always ending with a trailing run. */
export function trailingThrottle(run: () => void, windowMs: number): Throttled {
  let timer: ReturnType<typeof setTimeout> | null = null;
  let queued = false;

  const open = () => {
    timer = setTimeout(() => {
      timer = null;
      if (!queued) return;
      queued = false;
      run();
      open();
    }, windowMs);
  };

  const throttled = (() => {
    if (timer !== null) {
      queued = true;
      return;
    }
    run();
    open();
  }) as Throttled;

  throttled.cancel = () => {
    if (timer !== null) clearTimeout(timer);
    timer = null;
    queued = false;
  };
  return throttled;
}
