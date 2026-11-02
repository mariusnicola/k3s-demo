import type { Batch } from "../../../api/types";
import { StateBadge, type Column } from "../../../ds";

export const batchPath = (b: Batch) => `/batches/${encodeURIComponent(b.farmId)}/${encodeURIComponent(b.id)}`;
export const farmColumn: Column<Batch> = { key: "farm", header: "Farm", render: (b) => b.farmId };
export const stateColumn: Column<Batch> = { key: "state", header: "State", render: (b) => <StateBadge state={b.state} /> };
