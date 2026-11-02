import { useEffect, useState } from "react";
import { can, matchesAttention } from "../../logic/batch/batchRules";
import { useNow } from "../../live/clock";
import { Button, PageHeader, useDocumentTitle } from "../../ds";
import { MissedBanner } from "../shared";
import { BatchListPanel, type HeldBatches } from "./BatchListPanel";
import { BatchTotals } from "./BatchTotals";
import { CreateBatchDialog } from "./CreateBatchDialog";
import { useBatchFilters } from "./useBatchFilters";
import { useBatchList } from "./useBatchList";

function BatchesHeader({ canWrite, onCreate }: { canWrite: boolean; onCreate: () => void }) {
  return (
    <PageHeader
      title="Batches"
      subtitle="Every batch you can reach, from creation to close."
      actions={
        <Button variant="primary" icon="plus" disabledReason={canWrite ? null : "Creating a batch needs the Operator or Admin role."} onClick={onCreate}>
          New batch
        </Button>
      }
    />
  );
}

export function BatchesPage() {
  const filters = useBatchFilters();
  const { me, state, farms, farm, attention } = filters;
  const [creating, setCreating] = useState(false);
  // New batches land on top, so without this a running line moves rows under the pointer and takes the focused link.
  const [held, setHeld] = useState<HeldBatches | null>(null);
  const now = useNow();
  useDocumentTitle("Batches");

  useEffect(() => setHeld(null), [state, farm, attention]);

  const { list, feed, lostRead, summary, batches } = useBatchList(state, farm);
  const canWrite = can(me, "batch.write");
  const rows = (batches ?? []).filter((b) => matchesAttention(b, attention, now));

  return (
    <>
      <BatchesHeader canWrite={canWrite} onCreate={() => setCreating(true)} />

      <BatchTotals summary={summary} feed={feed} />

      {lostRead.missed ? <MissedBanner onRead={lostRead.readAgain} /> : null}

      <BatchListPanel missed={lostRead.missed} filters={filters} list={list} batches={batches} rows={rows} held={held} setHeld={setHeld} feed={feed} now={now} canWrite={canWrite} onCreate={() => setCreating(true)} />

      {creating ? <CreateBatchDialog open={creating} onClose={() => setCreating(false)} farms={farms} defaultFarm={farm || farms[0] || ""} /> : null}
    </>
  );
}
