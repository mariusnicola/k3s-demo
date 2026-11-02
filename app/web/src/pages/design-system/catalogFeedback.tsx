import { Banner, Button, LiveNote, CrashState, EmptyState, ErrorBoundary, ErrorState, OperationToasts, ReportId, ReportLogsLink, Skeleton, Spinner, Text, type OperationItem } from "../../ds";
import type { CatalogItem } from "./catalogItem";
import { TONES, noop } from "./catalogSamples";

const RUNNING_CHECKLIST: OperationItem[] = [{ id: "a", state: "running", title: "Deploy the cluster and the app", name: "Deploy the cluster and the app", detail: null, elapsed: "2 min 10 s", eta: "About 3 min left", steps: [{ name: "Build images", mark: "done", note: "Done" }, { name: "Charts", mark: "running", note: "Running, 40 s" }, { name: "Smoke test", mark: "pending", note: "Not started" }] }];
const FAILED_CHECKLIST: OperationItem[] = [{ id: "a", state: "failed", title: "Deploy the cluster and the app failed", name: "Deploy the cluster and the app", detail: null, elapsed: null, steps: [{ name: "Build images", mark: "done", note: "Done" }, { name: "Charts", mark: "failed", note: "Failed after 1 min" }, { name: "Smoke test", mark: "pending", note: "Not started" }], explanation: { what: "Stopped at step Charts.", where: "Work on the cluster", next: "Check the pods on the Dashboard, then run it again." } }];

function operationItems(): CatalogItem[] {
  return [
    {
      name: "OperationToasts",
      states: [
        { label: "running", render: () => <OperationToasts inline items={[{ id: "a", state: "running", title: "Deploy the cluster and the app", detail: "Charts", elapsed: "1m 20s" }]} /> },
        { label: "no signal", render: () => <OperationToasts inline items={[{ id: "a", state: "no-signal", title: "No signal from Deploy the cluster and the app", detail: "State unknown.", elapsed: "4m" }]} /> },
        { label: "done", render: () => <OperationToasts inline items={[{ id: "a", state: "done", title: "Deploy the cluster and the app finished", detail: null, elapsed: null }]} /> },
        { label: "failed", render: () => <OperationToasts inline items={[{ id: "a", state: "failed", title: "Deploy the cluster and the app failed", detail: "Charts", elapsed: null, onSeen: () => undefined }]} /> },
        { label: "checklist", render: () => <OperationToasts inline items={RUNNING_CHECKLIST} /> },
        { label: "checklist failed", render: () => <OperationToasts inline items={FAILED_CHECKLIST} /> },
        { label: "state unknown", render: () => <OperationToasts inline items={[{ id: "a", state: "no-signal", title: "Cluster work state unknown", detail: "The monitor is not answering.", elapsed: null }]} /> },
      ],
    },
  ];
}

function bannerItems(): CatalogItem[] {
  return [
    {
      name: "Banner",
      states: [
        ...TONES.map((tone) => ({ label: tone, render: () => <Banner tone={tone} title={`${tone} title`} live="off">Message text.</Banner> })),
        { label: "with action", render: () => <Banner tone="danger" title="Someone else changed this batch" live="off" actions={<Button variant="primary">Reload batch</Button>}>Nothing was saved.</Banner> },
      ],
    },
  ];
}

function waitingItems(): CatalogItem[] {
  return [
    { name: "Spinner", states: [{ label: "default", render: () => <Spinner label="Restoring your session" /> }] },
    { name: "Skeleton", states: [{ label: "loading", render: () => <Skeleton rows={3} /> }] },
    {
      name: "LiveNote",
      states: [{ label: "with a note", render: () => <LiveNote>Totals are from 12:04:31.</LiveNote> }, { label: "reserved, empty", render: () => <LiveNote /> }],
    },
    {
      name: "EmptyState",
      states: [
        { label: "message", render: () => <EmptyState title="No open alerts">Nothing needs action.</EmptyState> },
        { label: "with action", render: () => <EmptyState title="No batches yet" action={<Button variant="primary">Create the first batch</Button>} /> },
      ],
    },
  ];
}

function failureItems(): CatalogItem[] {
  return [
    {
      name: "ErrorState",
      states: [{ label: "with way out", render: () => <ErrorState title="Cannot reach the server" action={<Button>Try again</Button>}>Check the connection.</ErrorState> }],
    },
    {
      name: "ReportId",
      states: [
        { label: "ID and copy", render: () => <ReportId id={SAMPLE_REPORT} /> },
        { label: "with a link to Logs", render: () => <ReportLogsLink.Provider value><ReportId id={SAMPLE_REPORT} /></ReportLogsLink.Provider> },
      ],
    },
    {
      name: "ReportLogsLink",
      states: [{ label: "on, for a user Logs lets in", render: () => <ReportLogsLink.Provider value><ReportId id={SAMPLE_REPORT} /></ReportLogsLink.Provider> }],
    },
    {
      name: "CrashState",
      states: [
        { label: "with way out", render: () => <CrashState onReload={noop} /> },
        { label: "with its report", render: () => <CrashState onReload={noop} reportId={SAMPLE_REPORT} /> },
      ],
    },
    {
      name: "ErrorBoundary",
      states: [{ label: "no fault, shows its children", render: () => <ErrorBoundary><Text>Page content</Text></ErrorBoundary> }],
    },
  ];
}

const SAMPLE_REPORT = "6f1c2a34-1b2c-4d3e-8f90-a1b2c3d4e5f6";

export function feedbackItems(): CatalogItem[] {
  return [...operationItems(), ...bannerItems(), ...waitingItems(), ...failureItems()];
}
