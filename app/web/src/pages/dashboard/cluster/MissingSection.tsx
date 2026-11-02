import { EmptyState, Skeleton } from "../../../ds";

/** What a panel shows before its first report. */
export function monitorPlaceholder(lost: boolean) {
  return lost ? (
    <EmptyState icon="info" title="Unknown">
      The monitor is not answering. See the Health panel.
    </EmptyState>
  ) : (
    <Skeleton rows={3} label="Waiting for the first monitor report" />
  );
}

export function MissingSection({ what }: { what: string }) {
  return (
    <EmptyState icon="info" title={`No ${what} in the monitor report`}>
      The monitor could not read this from the cluster. See the Health panel.
    </EmptyState>
  );
}
