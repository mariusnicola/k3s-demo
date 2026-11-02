import type { Alert } from "../../api/types";
import { useSession } from "../../auth/session";
import { formatDateTime, shortId } from "../../logic/format";
import { can } from "../../logic/batch/batchRules";
import { AppLink, DataTable, ErrorDetail, IdText, KeyValues, StatusBadge, type Column, type ErrorExplanation } from "../../ds";
import { useCanOpenLogs } from "../shared";

function alertLabel(type: string): string {
  return type === "Dose2Overdue" ? "Dose 2 overdue" : type === "Blocked" ? "Blocked" : type;
}

function explainAlert(alert: Alert, canReplay: boolean, canWrite: boolean): ErrorExplanation {
  const where = `${alert.product} on ${alert.farmId}`;
  const technical = alert.detail ? <KeyValues items={[{ label: "Recorded detail", value: alert.detail }]} /> : undefined;
  const common = { where, correlationId: alert.correlationId, technical };
  if (alert.type === "Blocked") return { ...common, what: "The Worker refused an event of this batch and it went to dead letters.", impact: "The batch stays Blocked until the event is replayed.", next: canReplay ? "Open the batch and replay the event." : "Ask an admin to replay the event." };
  if (alert.type === "Dose2Overdue") return { ...common, what: "Dose 2 was not recorded inside its window.", impact: "The nightly run flagged the batch.", next: canWrite ? "Open the batch and check Dose 2." : "Ask an operator to check Dose 2." };
  return { ...common, what: `Alert ${alert.type} was raised on this batch.`, next: "Open the batch and check its state." };
}

function AlertDetail({ alert }: { alert: Alert }) {
  const logsLink = useCanOpenLogs();
  const { me } = useSession();
  return <ErrorDetail explanation={explainAlert(alert, logsLink, can(me, "batch.write"))} logsLink={logsLink} />;
}

const columns: Column<Alert>[] = [
  {
    key: "type",
    header: "Alert",
    render: (a) => (
      <StatusBadge tone={a.type === "Blocked" ? "danger" : "warning"} icon={a.type === "Blocked" ? "blocked" : "flag"}>
        {alertLabel(a.type)}
      </StatusBadge>
    ),
  },
  { key: "product", header: "Product", render: (a) => <AppLink to={`/batches/${encodeURIComponent(a.farmId)}/${encodeURIComponent(a.batchId)}`}>{a.product}</AppLink> },
  { key: "batch", header: "Batch", render: (a) => <IdText value={a.batchId} short={shortId(a.batchId)} /> },
  { key: "farm", header: "Farm", render: (a) => a.farmId },
  { key: "at", header: "Raised", render: (a) => formatDateTime(a.at) },
];

export function AlertsTable({ alerts }: { alerts: Alert[] }) {
  return <DataTable caption="Open alerts" columns={columns} rows={alerts} rowKey={(a) => `${a.batchId}-${a.type}-${a.at}`} detail={{ header: "Details", label: (a) => `details of ${alertLabel(a.type)} on ${a.product}`, render: (a) => <AlertDetail alert={a} /> }} />;
}
