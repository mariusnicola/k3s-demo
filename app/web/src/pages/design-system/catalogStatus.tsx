import { Cluster, ErrorDetail, KeyValues, Stat, StateBadge, StateRail, StatusBadge } from "../../ds";
import type { CatalogItem } from "./catalogItem";
import { TONES } from "./catalogSamples";

function badgeItems(): CatalogItem[] {
  return [
    {
      name: "StatusBadge",
      states: [
        {
          label: "tones",
          render: () => (
            <Cluster gap={2}>
              {TONES.map((tone) => (
                <StatusBadge key={tone} tone={tone} icon="info">
                  {tone}
                </StatusBadge>
              ))}
            </Cluster>
          ),
        },
      ],
    },
  ];
}

function batchStateItems(): CatalogItem[] {
  return [
    {
      name: "StateBadge",
      states: [
        {
          label: "every state",
          render: () => (
            <Cluster gap={2}>
              {(["Created", "Dose1", "Dose2", "Inspected", "Shipped", "Closed", "Rejected"] as const).map((s) => (
                <StateBadge key={s} state={s} />
              ))}
            </Cluster>
          ),
        },
      ],
    },
    {
      name: "StateRail",
      states: [
        { label: "first step", render: () => <StateRail state="Created" /> },
        { label: "middle", render: () => <StateRail state="Inspected" /> },
        { label: "done", render: () => <StateRail state="Closed" /> },
        { label: "rejected", render: () => <StateRail state="Rejected" /> },
      ],
    },
  ];
}

function statItems(): CatalogItem[] {
  return [
    {
      name: "Stat",
      states: [
        { label: "zero", render: () => <Stat label="Blocked" value={0} tone="danger" /> },
        { label: "alarm", render: () => <Stat label="Blocked" value={3} tone="danger" to="/" /> },
        { label: "warning", render: () => <Stat label="Flagged" value={7} tone="warning" /> },
        { label: "huge number", render: () => <Stat label="Closed" value={1234567890} /> },
        { label: "unknown", render: () => <Stat label="Rejected" value={null} tone="danger" /> },
        { label: "with detail", render: () => <Stat label="Closed" value={42} detail="1.5 per minute" /> },
        { label: "hero", render: () => <Stat hero label="Events created" value={1812} /> },
        { label: "hero text", render: () => <Stat hero label="Checks healthy" text="27 of 27" /> },
      ],
    },
  ];
}

function errorDetailItems(): CatalogItem[] {
  return [
    {
      name: "ErrorDetail",
      states: [
        {
          label: "every row",
          render: () => (
            <ErrorDetail
              explanation={{ what: "The Worker refused this event and it went to dead letters.", where: "Batch 7c41e2 on farm-a, at Dose 1", impact: "No step can happen until it is replayed.", next: "Replay the event.", correlationId: "4f1a09c2d7e84b6d9a10c3f5e2b7d681", reportId: "6f1c2a34-1b2c-4d3e-8f90-a1b2c3d4e5f6" }}
              logsLink
            />
          ),
        },
        { label: "message only", render: () => <ErrorDetail explanation={{ what: "The server did not answer." }} /> },
        {
          label: "with technical detail",
          render: () => <ErrorDetail explanation={{ what: "The Line gave up waiting for the previous event of a batch.", technical: <KeyValues items={[{ label: "Exception type", value: "TaskCanceledException" }]} /> }} />,
        },
      ],
    },
  ];
}

export function statusItems(): CatalogItem[] {
  return [...badgeItems(), ...batchStateItems(), ...statItems(), ...errorDetailItems()];
}
