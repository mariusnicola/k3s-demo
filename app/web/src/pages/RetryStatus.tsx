import { useSyncExternalStore } from "react";
import { readRetryNotice, subscribeRetries } from "../api/retry";
import { MAX_ATTEMPTS } from "../logic/backoff";
import { Spinner } from "../ds";

/** Lives in the shell, so a restart under any page reads as a wait, not a silent hang. */
export function RetryStatus() {
  const notice = useSyncExternalStore(subscribeRetries, readRetryNotice);
  if (!notice) return null;
  const next = notice.nextInMs === null ? "" : `, next try in ${Math.ceil(notice.nextInMs / 1000)} s`;
  return <Spinner label={`Retrying, attempt ${notice.attempt} of ${MAX_ATTEMPTS}${next}`} />;
}
