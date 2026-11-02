import type { Dispatch, SetStateAction } from "react";
import { BATCH_STATES, type Batch } from "../../api/types";
import { BATCH_LIST_LIMIT } from "../../config";
import { type Feed } from "../../live/useLiveSocket";
import type { Resource } from "../../live/useResource";
import { STATE_LABELS } from "../../logic/batch/batchRules";
import { formatCount, formatTime, shownTime } from "../../logic/format";
import { AppLink, Button, ConnectionStatus, DataTable, EmptyState, Grid, Panel, Select, Skeleton, Text } from "../../ds";
import { ProblemBanner, ProblemState } from "../shared";
import { batchColumns } from "./batchColumns";
import { ATTENTION_OPTIONS, type useBatchFilters } from "./useBatchFilters";

export interface HeldBatches {
  rows: Batch[];
  at: number;
}

interface BatchListProps {
  missed: boolean;
  filters: ReturnType<typeof useBatchFilters>;
  list: Resource<Batch[]>;
  batches: Batch[] | undefined;
  rows: Batch[];
  held: HeldBatches | null;
  setHeld: Dispatch<SetStateAction<HeldBatches | null>>;
  feed: Feed;
  now: number;
  canWrite: boolean;
  onCreate: () => void;
}

function BatchFilterFields({ filters: { state, farm, attention, farms, setFilter } }: Pick<BatchListProps, "filters">) {
  return (
    <Grid min="11rem">
      <Select label="State" value={state} onChange={(e) => setFilter("state", e.target.value)} options={[{ value: "", label: "All states" }, ...BATCH_STATES.map((s) => ({ value: s, label: STATE_LABELS[s] }))]} />
      <Select label="Farm" value={farm} onChange={(e) => setFilter("farm", e.target.value)} options={[{ value: "", label: "All my farms" }, ...farms.map((f) => ({ value: f, label: f }))]} />
      <Select label="Attention" value={attention} onChange={(e) => setFilter("attention", e.target.value)} options={ATTENTION_OPTIONS} />
    </Grid>
  );
}

function NoBatches({ filters: { state, farm, attention, clearFilters }, canWrite, onCreate }: Pick<BatchListProps, "filters" | "canWrite" | "onCreate">) {
  const filtersOn = Boolean(state || farm || attention);
  return filtersOn ? (
    <EmptyState title="No batches match these filters" action={<Button onClick={clearFilters}>Clear filters</Button>}>
      Change a filter, or clear all of them.
    </EmptyState>
  ) : (
    <EmptyState
      title="No batches yet"
      action={
        canWrite ? (
          <Button variant="primary" icon="plus" onClick={onCreate}>
            Create the first batch
          </Button>
        ) : undefined
      }
    >
      {canWrite ? "A batch starts in Created and walks through to Closed." : "Nothing has been created on your farms yet."}
    </EmptyState>
  );
}

function BatchTable({ rows, held, setHeld, now }: Pick<BatchListProps, "rows" | "held" | "setHeld" | "now">) {
  return (
    <>
      <DataTable caption="Batches" columns={batchColumns(held?.at ?? now)} rows={held?.rows ?? rows} rowKey={(b) => `${b.farmId}/${b.id}`} onHold={(holding) => setHeld(holding ? (current) => current ?? { rows, at: now } : null)} />
      {held ? (
        <Text size="xs" tone="secondary" as="p">
          List held while the pointer or focus is inside it.
        </Text>
      ) : null}
    </>
  );
}

function BatchListBody(props: BatchListProps) {
  const { list, batches, rows, held } = props;
  return (
    list.error && !list.data ? (
      <ProblemState error={list.error} onRetry={list.reload} />
    ) : !batches ? (
      <Skeleton rows={6} label="Loading batches" />
    ) : (held?.rows ?? rows).length === 0 ? (
      <NoBatches filters={props.filters} canWrite={props.canWrite} onCreate={props.onCreate} />
    ) : (
      <BatchTable rows={rows} held={held} setHeld={props.setHeld} now={props.now} />
    )
  );
}

export function BatchListPanel(props: BatchListProps) {
  const { filters, list, batches, held, feed, now, missed } = props;
  return (
    <Panel
      title="Batch list"
      description={held ? `Held at ${formatTime(held.at)}` : list.updatedAt ? `Updated ${formatTime(shownTime(missed, list.updatedAt, feed.state.lastDataAt))}` : undefined}
      actions={<ConnectionStatus label="Live updates" state={feed.state} now={now} onReconnect={feed.reconnect} />}
    >
      <BatchFilterFields filters={filters} />

      {list.error && list.data ? <ProblemBanner error={list.error} onRetry={list.reload} /> : null}

      <BatchListBody {...props} />
      {batches && batches.length >= BATCH_LIST_LIMIT ? (
        <Text size="xs" tone="secondary" as="p">
          Showing the newest {formatCount(BATCH_LIST_LIMIT)} batches. Older ones stay hidden until you filter by state or farm. The Attention filter only looks at these rows; <AppLink to="/alerts">Alerts</AppLink> lists every blocked and flagged batch.
        </Text>
      ) : null}
    </Panel>
  );
}
