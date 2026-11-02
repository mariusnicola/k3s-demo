import type { ReactNode } from "react";
import { type MonitorReport, type PodReport } from "../../../api/monitorTypes";
import { podPhaseTone, podsByConcern } from "../../../logic/feed/hops";
import { formatQuantity } from "../../../logic/format";
import { Banner, DataTable, ErrorDetail, IdText, Panel, Stack, StatusBadge, Text } from "../../../ds";
import { MissingSection } from "./MissingSection";

function PodReady({ pod, fresh }: { pod: PodReport; fresh: boolean }) {
  if (pod.ready === undefined) return <Text tone="muted">Not reported</Text>;
  if (!fresh) return <StatusBadge tone="neutral">{pod.ready ? "Was ready" : "Was not ready"}</StatusBadge>;
  return pod.ready ? <StatusBadge tone="success">Ready</StatusBadge> : <StatusBadge tone="warning">Not ready</StatusBadge>;
}

function PodsTable({ pods, fresh }: { pods: PodReport[]; fresh: boolean }) {
  const failed = fresh ? pods.filter((p) => p.phase === "Failed") : [];
  return (
    <Stack gap={2}>
      {failed.length > 0 ? (
        <Banner tone="danger" title={`${failed.length} ${failed.length === 1 ? "pod" : "pods"} failed`}>
          <ErrorDetail explanation={{ what: failed.map((p) => p.name).join(", "), where: [...new Set(failed.map((p) => p.node || "Not placed"))].join(", "), next: "Check the events of the pod in the cluster, then restart or replace it." }} />
        </Banner>
      ) : null}
      <DataTable
        caption="Pods"
        columns={[
          { key: "name", header: "Pod", render: (p) => <IdText value={p.name} /> },
          { key: "node", header: "Node", render: (p) => p.node || <Text tone="muted">Not placed</Text> },
          { key: "phase", header: "Phase", render: (p) => <StatusBadge tone={fresh ? podPhaseTone(p.phase) : "neutral"}>{fresh ? p.phase : `Was ${p.phase}`}</StatusBadge> },
          { key: "ready", header: "Ready", render: (p) => <PodReady pod={p} fresh={fresh} /> },
          { key: "cpu", header: "CPU used of limit", align: "end", render: (p) => `${formatQuantity(p.cpuUsageMilli, "m")} of ${formatQuantity(p.cpuLimitMilli, "m")}` },
          { key: "memory", header: "Memory used of limit", align: "end", render: (p) => `${formatQuantity(p.memoryUsageMi, "Mi")} of ${formatQuantity(p.memoryLimitMi, "Mi")}` },
        ]}
        rows={podsByConcern(pods)}
        rowKey={(p) => `${p.namespace}/${p.name}`}
      />
    </Stack>
  );
}

export function PodsPanel({ report, fresh, staleNote, placeholder }: { report: MonitorReport | null; fresh: boolean; staleNote: string | undefined; placeholder: ReactNode }) {
  return (
    <Panel title="Pods" description={staleNote}>
      {!report ? (
        placeholder
      ) : !report.pods || report.pods.length === 0 ? (
        <MissingSection what="pods" />
      ) : (
        <PodsTable pods={report.pods} fresh={fresh} />
      )}
    </Panel>
  );
}
