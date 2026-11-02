import { FLOW_STATES, type Batch } from "../../api/types";
import { STATE_LABELS, nextStep } from "../../logic/batch/batchRules";
import { Banner, Button, Panel, Text } from "../../ds";
import type { Prompt } from "./useBatchActions";

interface NextStepPanelProps {
  batch: Batch;
  gate: string | null;
  busy: boolean;
  setPrompt: (prompt: Prompt) => void;
  advanceTo: (to: Batch["state"]) => void;
}

export function NextStepPanel({ batch, gate, busy, setPrompt, advanceTo }: NextStepPanelProps) {
  const next = nextStep(batch.state);
  return (
    <Panel title="Next step">
      {next.kind === "final" ? (
        <Banner tone={batch.state === "Rejected" ? "danger" : batch.pending ? "warning" : "success"} title={batch.state === "Rejected" ? "Rejected, final" : "Closed, final"} live="off">
          {batch.state === "Rejected" ? `Reason: ${batch.reason ?? "not recorded"}` : batch.pending ? "No further step exists." : "This batch is complete. No further step exists."}
          {batch.pending ? <Text as="p">Its last compliance record is not written yet.</Text> : null}
        </Banner>
      ) : next.kind === "inspect" ? (
        <Button variant="primary" disabledReason={gate} onClick={() => setPrompt("inspect")}>
          Record inspection
        </Button>
      ) : (
        <Button variant="primary" icon="chevronRight" loading={busy} disabledReason={gate} onClick={() => (next.irreversible ? setPrompt("close") : advanceTo(next.to))}>
          Move to {STATE_LABELS[next.to]}
        </Button>
      )}
      <Text size="xs" tone="secondary" as="p">
        Flow: {FLOW_STATES.map((s) => STATE_LABELS[s]).join(", then ")}.
      </Text>
    </Panel>
  );
}
