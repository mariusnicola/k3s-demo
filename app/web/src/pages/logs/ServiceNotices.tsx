import type { LogFilters, ServiceLogs } from "../../api/types";
import type { Resource } from "../../live/useResource";
import { formatCount, formatTime } from "../../logic/format";
import { BROWSER_REASON, readsLine, readsServiceLog } from "../../logic/monitor/logs";
import { Banner, Button, Text } from "../../ds";
import { ProblemBanner } from "../shared";

function note(text: string) {
  return (
    <Text size="xs" tone="secondary" as="p">
      {text}
    </Text>
  );
}

function ServiceCounts({ data }: { data: ServiceLogs }) {
  return (
    <>
      {data.droppedByLimit > 0 ? note(`Showing ${formatCount(data.entries.length)} of ${formatCount(data.matched)} matching service entries and browser reports, the newest of each kind.`) : null}
      {data.evicted > 0 ? (
        <Banner tone="warning" title={`${formatCount(data.evicted)} older service entries left memory`} live="off">
          They are gone and cannot be shown.
        </Banner>
      ) : null}
      {data.browserEvicted > 0 ? (
        <Banner tone="warning" title={`${formatCount(data.browserEvicted)} older browser reports left memory`} live="off">
          They are gone and cannot be shown.
        </Banner>
      ) : null}
    </>
  );
}

/** The controls that keep the service log out, so the note names what to change and nothing else. */
function blockingControls({ source, level, reason }: LogFilters): string {
  const names = [...(source === "line" ? ["Source"] : []), ...(level === "info" ? ["Level"] : []), ...(reason && reason !== BROWSER_REASON ? ["Reason"] : [])];
  return names.length > 1 ? `${names.slice(0, -1).join(", ")} or ${names.at(-1)}` : (names[0] ?? "");
}

/** Nothing pushes service entries, so unlike the Line's rows these age until someone reads again. */
export function ServiceNotices({ services, filters, lineRows }: { services: Resource<ServiceLogs>; filters: LogFilters; lineRows: boolean }) {
  if (!readsServiceLog(filters) && !readsLine(filters)) return note(`No log was read: ${filters.reportId ? "a report" : "a browser report"} is only in the service log, which these filters leave out. Change ${blockingControls(filters)}.`);
  if (!readsServiceLog(filters)) return filters.source === "line" ? null : note("Only the production line log was read: this filter cannot match a service entry.");
  const { data, error, loading, updatedAt, reload } = services;
  return (
    <>
      {error ? <ProblemBanner error={error} onRetry={reload} /> : null}
      {error && !data && lineRows ? note("Showing production line entries only.") : null}
      {error && data ? note("Service entries here are from the last good read.") : null}
      {data && !error ? note(`Service entries do not update live, and the Dashboard does not count them. Read at ${formatTime(updatedAt ?? Date.now())}.`) : null}
      {data && data.entries.length === 0 && (filters.farm || filters.batch) ? note("No service entry names this farm or batch. Many name neither, so a service may still be involved.") : null}
      {data && !error ? (
        <Button icon="refresh" loading={loading} onClick={() => void reload()}>
          Read service entries again
        </Button>
      ) : null}
      {data ? <ServiceCounts data={data} /> : null}
    </>
  );
}
