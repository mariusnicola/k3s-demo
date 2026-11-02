import { readFileSync, readdirSync } from "node:fs";
import { resolve } from "node:path";
import { renderInRouter } from "../mocks/harness";
import { CatalogView, buildCatalog } from "../pages/design-system/catalog";
import * as ds from "./index";

/** Each stateful component names the states it must show. A new state goes here and in the catalog. */
const REQUIRED_STATES: Record<string, string[]> = {
  Button: ["primary", "secondary", "danger", "ghost", "loading", "disabled", "disabled with reason", "long text"],
  IconButton: ["default", "disabled"],
  TextInput: ["default", "error", "disabled"],
  TextArea: ["default", "error", "disabled"],
  Select: ["default", "error", "disabled"],
  Checkbox: ["checked", "unchecked", "disabled"],
  RadioGroup: ["default", "with help", "disabled"],
  SegmentedControl: ["default", "with help", "disabled"],
  Lever: ["at zero", "with help", "at the top", "failed", "disabled"],
  Banner: ["neutral", "info", "success", "warning", "danger", "with action"],
  OperationToasts: ["running", "no signal", "done", "failed", "state unknown", "checklist", "checklist failed"],
  ConnectionStatus: ["connecting", "open", "stale", "lost", "lost, never connected"],
  StateRail: ["first step", "middle", "done", "rejected"],
  Stat: ["zero", "alarm", "huge number", "unknown", "with detail", "hero", "hero text"],
  LineChart: ["one series", "four series", "gap in the line", "no buckets", "small values"],
  Meter: ["zero", "normal", "breach", "stale", "reserved"],
  CountFilterList: ["none selected", "one selected"],
  DataTable: ["with expandable rows", "rows and long text", "tiles"],
  KeyValues: ["default", "figures"],
  ConfirmDialog: ["default, opens on press, confirm shows busy", "danger, opens on press, confirm shows busy"],
  ProblemBanner: ["400 with Dismiss", "401", "403", "404", "409", "412", "428", "500", "network"],
  ProblemState: ["404", "network"],
  EventBanner: ["in flight", "delayed", "blocked", "blocked, replay running", "blocked, viewer cannot replay"],
  PartTip: ["with a live line", "no live line"],
  ArchitectureDiagram: ["live with dots", "compact", "lost", "no monitor report", "scaled pods in every state", "stale report"],
  SplitLayout: ["main with aside"],
  ErrorDetail: ["every row", "message only", "with technical detail"],
  CopyButton: ["default"],
  FlowMap: ["with batches", "empty"],
  EmptyState: ["message", "with action"],
};

const NON_COMPONENTS = /^(use|apply)|^[A-Z_]+$/;

function exportedComponents(): string[] {
  return Object.keys(ds).filter((name) => /^[A-Z]/.test(name) && !NON_COMPONENTS.test(name));
}

describe("design system catalog", () => {
  const items = buildCatalog(Date.now());

  it("REQ-BT-034 finds the real components, so an empty list cannot pass", () => {
    expect(exportedComponents().length).toBeGreaterThan(30);
    expect(exportedComponents()).toContain("Button");
  });

  it.each(exportedComponents())("REQ-BT-034 %s is in the catalog", (name) => {
    expect(items.map((i) => i.name)).toContain(name);
  });

  it.each(Object.entries(REQUIRED_STATES))("REQ-BT-034 %s shows every required state", (name, states) => {
    const labels = items.find((i) => i.name === name)?.states.map((s) => s.label) ?? [];
    for (const state of states) expect(labels).toContain(state);
  });

  it("REQ-BT-034 every exported tsx component in ds is exported from the index", () => {
    const dir = resolve(__dirname);
    const declared = readdirSync(dir, { recursive: true, encoding: "utf8" })
      .filter((f) => f.endsWith(".tsx") && !f.endsWith(".test.tsx"))
      .flatMap((f) => [...readFileSync(resolve(dir, f), "utf8").matchAll(/^export function ([A-Z]\w+)/gm)].map((m) => m[1]!));
    expect(declared.filter((name) => !(name in ds))).toEqual([]);
  });

  it("REQ-BT-034 every catalog state renders without crashing", () => {
    renderInRouter(<CatalogView items={items} />);
    // By attribute, not role or text: those walk every node of a page that holds every component.
    for (const item of items) {
      const entry = document.querySelector(`[data-component="${item.name}"]`)!;
      const shown = [...entry.querySelectorAll("[data-state-label]")].map((state) => state.getAttribute("data-state-label"));
      expect(shown).toEqual(item.states.map((state) => state.label));
    }
  });
});
