import type { MonitorReport } from "../../../api/monitorTypes";
import type { ReactNode } from "react";
import { formatQuantity } from "../../../logic/format";
import { DataTable, Panel, StatusBadge } from "../../../ds";
import { MissingSection } from "./MissingSection";

function NodeReady({ ready, fresh }: { ready: boolean; fresh: boolean }) {
  return !fresh ? (
    <StatusBadge tone="neutral">{ready ? "Was ready" : "Was not ready"}</StatusBadge>
  ) : ready ? (
    <StatusBadge tone="success" icon="check">
      Ready
    </StatusBadge>
  ) : (
    <StatusBadge tone="danger" icon="close">
      Not ready
    </StatusBadge>
  );
}

function NodesTable({ nodes, fresh }: { nodes: NonNullable<MonitorReport["nodes"]>; fresh: boolean }) {
  return (
    <DataTable
      caption="Nodes"
      columns={[
        { key: "name", header: "Node", render: (n) => n.name },
        { key: "role", header: "Role", render: (n) => (n.role === "control plane" ? "Control plane" : "Worker") },
        { key: "ready", header: "Status", render: (n) => <NodeReady ready={n.ready} fresh={fresh} /> },
        { key: "cpu", header: "CPU used", align: "end", render: (n) => formatQuantity(n.cpuUsageMilli, "m") },
        { key: "memory", header: "Memory used of allocatable", align: "end", render: (n) => (n.allocatableMemoryMi === null ? formatQuantity(n.memoryUsageMi, "Mi") : `${formatQuantity(n.memoryUsageMi, "Mi")} of ${formatQuantity(n.allocatableMemoryMi, "Mi")}`) },
      ]}
      rows={nodes}
      rowKey={(n) => n.name}
    />
  );
}

export function NodesPanel({ report, fresh, staleNote, placeholder }: { report: MonitorReport | null; fresh: boolean; staleNote: string | undefined; placeholder: ReactNode }) {
  return (
    <Panel title="Nodes" description={staleNote}>
      {!report ? (
        placeholder
      ) : !report.nodes || report.nodes.length === 0 ? (
        <MissingSection what="nodes" />
      ) : (
        <NodesTable nodes={report.nodes} fresh={fresh} />
      )}
    </Panel>
  );
}
