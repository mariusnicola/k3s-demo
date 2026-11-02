import type { Batch } from "../../api/types";
import { formatDateTime } from "../../logic/format";
import { Button, IdText, KeyValues, Panel } from "../../ds";

export function DetailsPanel({ batch }: { batch: Batch }) {
  return (
    <Panel title="Details">
      <KeyValues
        items={[
          { label: "Batch ID", value: <IdText value={batch.id} /> },
          { label: "Created", value: formatDateTime(batch.createdAt) },
          { label: "Farm", value: batch.farmId },
          { label: "Steps", value: String(batch.steps.length) },
          { label: "Event", value: batch.pending ? <IdText value={batch.pending.eventId} /> : "None in flight" },
        ]}
      />
    </Panel>
  );
}

export function AdminPanel({ failNext, onFailNext }: { failNext: string | null; onFailNext: () => void }) {
  return (
    <Panel title="Admin" description="Makes the Worker refuse the next event of this batch, to show the dead-letter path.">
      <Button variant="danger" disabledReason={failNext} onClick={onFailNext}>
        Fail next delivery
      </Button>
    </Panel>
  );
}
