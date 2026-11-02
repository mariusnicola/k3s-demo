import type { ReactNode } from "react";

export interface CatalogItem {
  name: string;
  states: { label: string; render: () => ReactNode; inert?: boolean }[];
}
