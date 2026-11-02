import type { ReactNode } from "react";
import type { MonitorReport } from "../../../api/monitorTypes";
import { Banner, Meter, Panel, Stack, StatusBadge, Text } from "../../../ds";
import { formatCount } from "../../../logic/format";
import { MissingSection } from "./MissingSection";

const CAP_RESOURCE = {
  cpuMilli: { label: "CPU reserved", unit: "m" },
  memoryMi: { label: "memory", unit: "Mi" },
} as const;

function useLine(report: MonitorReport, cap: NonNullable<MonitorReport["caps"]>[number], fresh: boolean): string {
  const node = report.nodes?.find((n) => n.name === cap.node);
  const use = cap.resource === "cpuMilli" ? node?.cpuUsageMilli : node?.memoryUsageMi;
  const unit = CAP_RESOURCE[cap.resource].unit;
  const what = fresh ? "In use now" : "Last known use";
  return use == null ? `${what}: not reported` : `${what}: ${formatCount(use)} ${unit}`;
}

function CapMeters({ report, caps, fresh }: { report: MonitorReport; caps: NonNullable<MonitorReport["caps"]>; fresh: boolean }) {
  return (
    <Stack gap={3}>
      {report.capIssues && report.capIssues.length > 0 ? (
        <Banner tone={fresh ? "warning" : "neutral"} title={fresh ? "Live limits differ from caps.json" : "Limits differed from caps.json"} live="off">
          {report.capIssues.join(" ")}
        </Banner>
      ) : null}
      <Text tone="muted">The bars count limits at max replicas, not live use.</Text>
      {caps.map((cap) => (
        <Stack key={`${cap.node}-${cap.resource}`} gap={1}>
          <Meter label={`${cap.node} ${CAP_RESOURCE[cap.resource].label}`} value={cap.sum} max={cap.cap} unit={CAP_RESOURCE[cap.resource].unit} breach={cap.breach} stale={!fresh} reserved />
          <Text tone="muted">{useLine(report, cap, fresh)}</Text>
          {cap.breach ? (
            fresh ? (
              <StatusBadge tone="danger" icon="warning">
                Breach: over the cap
              </StatusBadge>
            ) : (
              <StatusBadge tone="neutral">Was over the cap</StatusBadge>
            )
          ) : null}
        </Stack>
      ))}
    </Stack>
  );
}

export function CapsPanel({ report, fresh, staleNote, placeholder }: { report: MonitorReport | null; fresh: boolean; staleNote: string | undefined; placeholder: ReactNode }) {
  return (
    <Panel title="Node caps" description={staleNote ?? "Sum of limits at max replicas, against each cap."}>
      {!report ? (
        placeholder
      ) : !report.caps || report.caps.length === 0 ? (
        <MissingSection what="caps" />
      ) : (
        <CapMeters report={report} caps={report.caps} fresh={fresh} />
      )}
    </Panel>
  );
}
