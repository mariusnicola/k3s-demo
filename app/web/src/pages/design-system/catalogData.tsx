import { useState } from "react";
import { ArchitectureDiagram, CopyButton, DataTable, FlowMap, IdText, KeyValues, Lever, LineChart, Meter, Named, PartTip, UnitGrid, Panel, SplitLayout, Stat, Timeline } from "../../ds";
import type { CatalogItem } from "./catalogItem";
import { CountFilterDemo, DetailTableDemo } from "./catalogDemos";
import { LONG_TEXT, chartBucket, chartSeries, counts, liveFrame, noFailures, noFrame, samplePods, steps, unknownHealth } from "./catalogSamples";

function overviewItems(): CatalogItem[] {
  return [
    {
      name: "FlowMap",
      states: [
        { label: "with batches", render: () => <FlowMap counts={{ Created: 12, Dose1: 30, Dose2: 8, Inspected: 4, Shipped: 2, Closed: 140, Rejected: 3 }} /> },
        { label: "empty", render: () => <FlowMap counts={{ Created: 0, Dose1: 0, Dose2: 0, Inspected: 0, Shipped: 0, Closed: 0, Rejected: 0 }} /> },
      ],
    },
    { name: "CopyButton", states: [{ label: "default", render: () => <CopyButton value="4f1a09c2" label="Copy correlation ID" /> }] },
    {
      name: "SplitLayout",
      states: [
        {
          label: "main with aside",
          render: () => (
            <SplitLayout aside={<Stat hero label="Checks healthy" text="27 of 27" />}>
              <Panel title="Main">The wide column.</Panel>
            </SplitLayout>
          ),
        },
      ],
    },
  ];
}

function LeverSample({ start, help, error, disabled }: { start: number; help?: string; error?: string; disabled?: boolean }) {
  const [value, setValue] = useState(start);
  return <Lever label="Requested rate" valueText={`${value} batches per second`} value={value} min={0} max={35} step={0.2} major={5} minor={1} help={help} error={error} disabled={disabled} onMove={setValue} onRelease={setValue} />;
}

const TIP_ITEM: CatalogItem = {
  name: "PartTip",
  states: [
    { label: "with a live line", render: () => <PartTip id="catalog-tip-live" info={{ name: "Worker", what: "The service that handles events. These scale up and down.", does: "It does the work for each event." }} live="worker-1-aaaaa: Ready, on k3s-node1" /> },
    { label: "no live line", render: () => <PartTip id="catalog-tip-plain" info={{ name: "This page", what: "Your screen.", does: "It talks to the system through the load balancer address, like any user." }} /> },
  ],
};

const LEVER_ITEM: CatalogItem = {
  name: "Lever",
  states: [
    { label: "at zero", render: () => <LeverSample start={0} /> },
    { label: "with help", render: () => <LeverSample start={7.4} help="The new rate goes to the line when you let go." /> },
    { label: "at the top", render: () => <LeverSample start={35} /> },
    { label: "failed", render: () => <LeverSample start={3} error="The new rate was not confirmed." /> },
    { label: "disabled", render: () => <LeverSample start={12} disabled /> },
  ],
};

function chartItems(): CatalogItem[] {
  return [
    {
      name: "UnitGrid",
      states: [
        { label: "one element each", render: () => <UnitGrid caption="Workers, sample" units={[{ id: "worker-1-aaaaa", name: "aaaaa", note: "k3s-node1, Ready", tone: "success", icon: "check", detail: { words: "Working in the last minute", icon: "pulse" } }, { id: "worker-1-bbbbb", name: "bbbbb", note: "k3s-node2, Not ready", tone: "warning", icon: "warning" }, { id: "worker-1-ccccc", name: "ccccc", note: "k3s-node2, Failed", tone: "danger", icon: "close" }]} /> },
      ],
    },
    LEVER_ITEM,
    TIP_ITEM,
    {
      name: "Named",
      states: [{ label: "short words, full name", render: () => <Named name="Follow trace 086529a782abe9fc53c3959ccd970732">086529a7...</Named> }],
    },
    {
      name: "LineChart",
      states: [
        { label: "one series", render: () => <LineChart caption="Events" series={chartSeries(["Events created"], [3, 5, 4, 7])} formatAt={chartBucket} /> },
        { label: "four series", render: () => <LineChart caption="Batches" series={chartSeries(["Created", "Shipped", "Closed", "Rejected"], [3, 5, 4, 7])} formatAt={chartBucket} /> },
        { label: "gap in the line", render: () => <LineChart caption="Doses" series={chartSeries(["Dose 1"], [3, null, 4, 7])} formatAt={chartBucket} /> },
        { label: "no buckets", render: () => <LineChart caption="Inspections" series={[]} formatAt={chartBucket} /> },
        { label: "small values", render: () => <LineChart caption="Time in queue" series={chartSeries(["Queue"], [0.012, 0.04, null, 0.02])} formatAt={chartBucket} formatValue={(seconds) => `${Math.round(seconds * 1000)} ms`} /> },
      ],
    },
  ];
}

function tableItems(): CatalogItem[] {
  return [
    {
      name: "DataTable",
      states: [
        { label: "with expandable rows", render: () => <DetailTableDemo /> },
        { label: "tiles", render: () => <DataTable tiles="labelled" caption="Example tiles" columns={[{ key: "a", header: "Product", render: (r: { a: string; b: number }) => r.a }, { key: "b", header: "Count", render: (r: { a: string; b: number }) => r.b }]} rows={[{ a: "Bovine vaccine", b: 3 }, { a: LONG_TEXT, b: 12 }]} rowKey={(r) => r.a} /> },
        {
          label: "rows and long text",
          render: () => (
            <DataTable
              caption="Example"
              columns={[
                { key: "a", header: "Product", render: (r: { a: string; b: number }) => r.a },
                { key: "b", header: "Count", align: "end", render: (r: { a: string; b: number }) => r.b },
              ]}
              rows={[
                { a: "Bovine vaccine", b: 12 },
                { a: LONG_TEXT, b: 0 },
              ]}
              rowKey={(r) => r.a}
            />
          ),
        },
      ],
    },
  ];
}

function measureItems(): CatalogItem[] {
  return [
    {
      name: "CountFilterList",
      states: [
        { label: "none selected", render: () => <CountFilterDemo /> },
        { label: "one selected", render: () => <CountFilterDemo initial="api-timeout" /> },
      ],
    },
    {
      name: "Meter",
      states: [
        { label: "zero", render: () => <Meter label="k3s-node2 memory" value={0} max={8192} unit="Mi" /> },
        { label: "normal", render: () => <Meter label="k3s-node2 memory" value={4096} max={8192} unit="Mi" /> },
        { label: "breach", render: () => <Meter label="k3s-node1 CPU" value={4500} max={4000} unit="m" breach /> },
        { label: "stale", render: () => <Meter label="k3s-node1 CPU" value={4500} max={4000} unit="m" breach stale /> },
        { label: "reserved", render: () => <Meter label="k3s-node2 CPU reserved" value={3000} max={4000} unit="m" reserved /> },
      ],
    },
  ];
}

function recordItems(): CatalogItem[] {
  return [
    {
      name: "IdText",
      states: [
        { label: "full", render: () => <IdText value="rec-1" /> },
        { label: "shortened", render: () => <IdText value="3f1c2a7e-0d44-4f7b-9d0a-1e8d4a0c9b11" short="3f1c2a7e..." /> },
        { label: "shortened, full on phones", render: () => <IdText value="3f1c2a7e-0d44-4f7b-9d0a-1e8d4a0c9b11" short="3f1c2a7e..." phoneFull /> },
      ],
    },
    { name: "KeyValues", states: [{ label: "figures", render: () => <KeyValues figures items={[{ label: "Started", value: "1,284" }, { label: "Closed", value: "1,190" }, { label: "Rejected", value: "94" }, { label: "In flight", value: "12,345,678" }]} /> }, { label: "default", render: () => <KeyValues items={[{ label: "Farm", value: "farm-a" }, { label: "Product", value: LONG_TEXT }]} /> }] },
    {
      name: "Timeline",
      states: [
        { label: "recorded and pending", render: () => <Timeline steps={steps} /> },
        { label: "rejected", render: () => <Timeline steps={[{ ...steps[0]!, state: "Rejected", recordId: "rec-9" }]} /> },
      ],
    },
  ];
}

function diagramItems(): CatalogItem[] {
  return [
    {
      name: "ArchitectureDiagram",
      states: [
        { label: "live with dots", render: () => <ArchitectureDiagram getFrame={liveFrame} counts={counts} failures={noFailures} health={{ ...unknownHealth, api: { state: "ok", text: "2 of 2 pods ready" }, cosmos: { state: "ok", text: "Healthy" }, bus: { state: "failing", text: "Failing" } }} summary="Example diagram with two traces" /> },
        { label: "compact", render: () => <ArchitectureDiagram getFrame={liveFrame} counts={counts} failures={{ ...noFailures, worker: 2 }} health={unknownHealth} compact summary="Compact example" /> },
        { label: "no monitor report", render: () => <ArchitectureDiagram getFrame={noFrame} counts={noFailures} failures={noFailures} health={unknownHealth} summary="No monitor report yet." pods={null} /> },
        { label: "scaled pods in every state", render: () => <ArchitectureDiagram getFrame={liveFrame} counts={counts} failures={noFailures} health={unknownHealth} summary="Every pod state." pods={samplePods} /> },
        { label: "stale report", render: () => <ArchitectureDiagram getFrame={noFrame} counts={counts} failures={noFailures} health={unknownHealth} summary="Last report, no longer current." pods={samplePods} stale /> },
        { label: "lost", render: () => <ArchitectureDiagram getFrame={noFrame} counts={noFailures} failures={noFailures} health={unknownHealth} lost summary="Stream lost example" /> },
      ],
    },
  ];
}

export function dataItems(): CatalogItem[] {
  return [...overviewItems(), ...chartItems(), ...tableItems(), ...measureItems(), ...recordItems(), ...diagramItems()];
}
