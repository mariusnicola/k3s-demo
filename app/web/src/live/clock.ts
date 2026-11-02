import { useEffect, useState } from "react";

/** Redraw tick for ages and staleness. Local only, it never touches the network. */
export function useNow(periodMs = 1_000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), periodMs);
    return () => clearInterval(timer);
  }, [periodMs]);
  return now;
}
