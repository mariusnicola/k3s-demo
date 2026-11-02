import { useState } from "react";
import { client } from "../../../api/client";
import type { ComplianceResult } from "../../../api/types";
import { useAction } from "../../../live/useAction";
import { formatCount, formatTime } from "../../../logic/format";
import { AppLink, Banner, Button, Cluster, Panel } from "../../../ds";
import { ProblemBanner } from "../../shared";

function NightlyResult({ ran }: { ran: ComplianceResult & { at: number } }) {
  return (
    <Banner tone="success" title={`Nightly run finished at ${formatTime(ran.at)}`}>
      {ran.flagged === 0 ? (
        "No batch needed a new flag."
      ) : (
        <>
          {formatCount(ran.flagged)} newly flagged. <AppLink to="/alerts">Open Alerts</AppLink>
        </>
      )}
    </Banner>
  );
}

export function NightlyPanel() {
  const action = useAction();
  const [ran, setRan] = useState<(ComplianceResult & { at: number }) | null>(null);
  const run = () =>
    void action.run(async () => {
      setRan(null);
      const { data } = await client.runCompliance();
      setRan({ ...data, at: Date.now() });
    });
  return (
    <Panel title="Nightly compliance" description="Flags Dose 1 batches past their window. Normally runs at 02:00.">
      <Cluster>
        <Button icon="play" loading={action.busy} onClick={run}>
          Run nightly now
        </Button>
      </Cluster>
      {ran ? <NightlyResult ran={ran} /> : null}
      {action.error ? <ProblemBanner error={action.error} onDismiss={action.clearError} /> : null}
    </Panel>
  );
}
