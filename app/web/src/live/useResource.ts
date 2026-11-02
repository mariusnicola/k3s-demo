import { useCallback, useEffect, useRef, useState, type Dispatch, type SetStateAction } from "react";

export interface Resource<T> {
  /** Last good value; kept during a reload and after a failed reload. */
  data: T | undefined;
  loading: boolean;
  error: unknown;
  /** Time the current data was fetched. */
  updatedAt: number | null;
  /** `loading` turns on and the promise settles when the read is done. */
  reload: () => Promise<T | undefined>;
}

export interface MissedRead {
  missed: boolean;
  readAgain: () => unknown;
}

/**
 * What a dropped socket missed never arrives. Reconnecting never reads by itself, so a page full of users
 * coming back does not become a burst of reads; it says so, and the user presses once.
 */
export function useMissedRead(status: string, read: { reload: () => unknown; data: unknown }): MissedRead {
  const [returned, setReturned] = useState(false);
  const [seen, setSeen] = useState(status);
  // Set during render, not in an effect: a commit without the offer would unmount the button under a press.
  if (status !== seen) {
    setSeen(status);
    if (seen === "lost") setReturned(true);
  }
  useEffect(() => setReturned(false), [read.data]);
  return { missed: status === "lost" || returned, readAgain: read.reload };
}

interface ResourceState<T> {
  data: T | undefined;
  loading: boolean;
  error: unknown;
  updatedAt: number | null;
}

/** A fresh object each time: a shared one would let React skip the reset render. */
function startingState<T>(): ResourceState<T> {
  return { data: undefined, loading: true, error: null, updatedAt: null };
}

function useReload<T>(load: (signal: AbortSignal) => Promise<T>, setState: Dispatch<SetStateAction<ResourceState<T>>>) {
  const latest = useRef(load);
  latest.current = load;
  const generation = useRef(0);
  const controller = useRef<AbortController | null>(null);

  const reload = useCallback((): Promise<T | undefined> => {
    const id = ++generation.current;
    controller.current?.abort();
    const abort = new AbortController();
    controller.current = abort;
    setState((s) => ({ ...s, loading: true }));
    return latest
      .current(abort.signal)
      .then((data) => {
        if (id === generation.current) setState({ data, loading: false, error: null, updatedAt: Date.now() });
        return data;
      })
      .catch((error: unknown) => {
        if (id === generation.current) setState((s) => ({ ...s, loading: false, error }));
        return undefined;
      });
  }, []);

  return { reload, generation, controller };
}

/** One async read with stale-response protection. It runs on load; after that only an operator's press or a refused write reads again. */
export function useResource<T>(load: (signal: AbortSignal) => Promise<T>, deps: readonly unknown[]): Resource<T> {
  const [state, setState] = useState<ResourceState<T>>(startingState);
  const { reload, generation, controller } = useReload(load, setState);

  useEffect(() => {
    setState(startingState());
    void reload();
    return () => {
      generation.current += 1;
      controller.current?.abort();
    };
    // Callers list what the load depends on, same contract as useEffect.
  }, [...deps, reload]);

  return { ...state, reload };
}
