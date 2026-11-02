import type { LineLogs } from "../../api/types";
import { LOG_CAPACITY } from "../../config";
import { type Feed } from "../../live/useLiveSocket";
import type { MissedRead, Resource } from "../../live/useResource";
import { formatCount, formatTime, newestTime } from "../../logic/format";
import { Banner, Button, Text } from "../../ds";
import { ProblemBanner } from "../shared";

interface EntriesNoticesProps {
  logs: Resource<LineLogs>;
  reply: LineLogs | null;
  feed: Feed;
  skipped: number;
  missed: boolean;
  lostRead: MissedRead;
  /** Rows are on screen, so a failed Line read must not take the whole panel. */
  shown: boolean;
}

function LineReadFailed() {
  return (
    <Text size="xs" tone="secondary" as="p">
      The production line log could not be read. Showing service entries only.
    </Text>
  );
}

export function EntriesNotices({ logs, reply, feed, skipped, missed, lostRead, shown }: EntriesNoticesProps) {
  return (
    <>
      {logs.error && (reply || shown) ? <ProblemBanner error={logs.error} onRetry={logs.reload} /> : null}
      {logs.error && !reply && shown ? <LineReadFailed /> : null}
      {feed.state.status === "lost" && reply ? (
        <Banner floating="page" tone="warning" title="Live updates are lost" live="off">
          Entries after {formatTime(missed ? logs.updatedAt : newestTime(feed.state.lastDataAt, logs.updatedAt))} are missing until the stream returns.
        </Banner>
      ) : null}
      {skipped > 0 ? (
        <Text size="xs" tone="secondary" as="p">
          Entries came too fast, so some were left out of this list.
        </Text>
      ) : null}
      {skipped > 0 || missed || lostRead.missed ? (
        <Button icon="refresh" loading={logs.loading} onClick={() => void lostRead.readAgain()}>
          {skipped > 0 ? "Read the newest entries again" : "Load missed entries"}
        </Button>
      ) : null}
      {reply && reply.evicted > 0 ? (
        <Banner tone="warning" title={`${formatCount(reply.evicted)} older entries left memory`} live="off">
          The line keeps its newest {formatCount(LOG_CAPACITY)} entries of every level. Older ones are gone and cannot be shown, though their errors still count on the Dashboard.
        </Banner>
      ) : null}
    </>
  );
}
