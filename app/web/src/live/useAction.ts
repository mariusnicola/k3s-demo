import { useCallback, useState } from "react";

export interface Action {
  busy: boolean;
  error: unknown;
  run: (work: () => Promise<unknown>) => Promise<boolean>;
  clearError: () => void;
}

/** One write at a time, with the failure held for the screen to show. */
export function useAction(): Action {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const run = useCallback(async (work: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await work();
      return true;
    } catch (failure) {
      setError(failure);
      return false;
    } finally {
      setBusy(false);
    }
  }, []);

  const clearError = useCallback(() => setError(null), []);
  return { busy, error, run, clearError };
}
