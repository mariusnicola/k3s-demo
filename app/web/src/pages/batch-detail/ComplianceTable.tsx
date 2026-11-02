import type { Step } from "../../api/types";
import { STATE_LABELS, chainLink } from "../../logic/batch/batchRules";
import { shortId } from "../../logic/format";
import { DataTable, EmptyState, IdText, StatusBadge, Text, type Column } from "../../ds";

function complianceColumns(steps: Step[]): Column<{ step: Step; index: number }>[] {
  return [
    { key: "step", header: "Step", render: (r) => STATE_LABELS[r.step.state] },
    { key: "record", header: "Record", render: (r) => <IdText value={r.step.recordId} short={shortId(r.step.recordId)} phoneFull /> },
    { key: "hash", header: "Hash", render: (r) => <IdText value={r.step.hash} short={shortId(r.step.hash)} phoneFull /> },
    { key: "previous", header: "Previous hash", render: (r) => (r.step.previousHash ? <IdText value={r.step.previousHash} short={shortId(r.step.previousHash)} phoneFull /> : <Text tone="muted">None, first record</Text>) },
    {
      key: "chain",
      header: "Chain",
      render: (r) => {
        const link = chainLink(steps, r.index);
        if (link === "broken") {
          return (
            <StatusBadge tone="danger" icon="warning">
              Link mismatch
            </StatusBadge>
          );
        }
        if (link === "unknown") return <Text tone="muted">Unknown</Text>;
        return (
          <StatusBadge tone="neutral" icon="chain">
            {link === "first" ? "Start of chain" : "Links to previous"}
          </StatusBadge>
        );
      },
    },
  ];
}

export function ComplianceTable({ steps }: { steps: Step[] }) {
  const rows = steps.map((step, index) => ({ step, index }));
  const recorded = rows.filter((r) => r.step.recordId);
  const columns = complianceColumns(steps);
  if (recorded.length === 0) {
    return (
      <EmptyState icon="chain" title="No compliance records yet">
        A record appears here once the Worker has handled the first step.
      </EmptyState>
    );
  }
  return <DataTable caption="Compliance records" columns={columns} rows={recorded} rowKey={(r) => String(r.index)} />;
}
