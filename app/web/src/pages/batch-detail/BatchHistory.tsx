import type { Step } from "../../api/types";
import { EmptyState, Panel, Tabs, Timeline } from "../../ds";
import { ComplianceTable } from "./ComplianceTable";

export function BatchHistory({ steps }: { steps: Step[] }) {
  return (
    <Panel>
      <Tabs
        label="Batch history"
        items={[
          {
            id: "timeline",
            label: "Timeline",
            content:
              steps.length === 0 ? (
                <EmptyState icon="clock" title="No steps yet">
                  The first step appears when the batch moves to Dose 1.
                </EmptyState>
              ) : (
                <Timeline steps={steps} />
              ),
          },
          { id: "records", label: "Compliance records", content: <ComplianceTable steps={steps} /> },
        ]}
      />
    </Panel>
  );
}
