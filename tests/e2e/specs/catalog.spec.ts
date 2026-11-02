import { AxeBuilder } from "@axe-core/playwright";
import type { Page } from "@playwright/test";
import { expect, test } from "../support/fixtures";
import { button, clippedControls, expectNoSideScroll, settle } from "../support/ui";

const COMPONENTS = ["PartTip", "Lever", "UnitGrid", "Named", "OperationToasts", "LiveNote", "ReportId", "ReportLogsLink", "ErrorDetail", "FlowMap", "CopyButton", "SplitLayout", "LineChart", "CountFilterList", "AppLink", "AppShell", "ArchitectureDiagram", "Banner", "Button", "CatalogEntry", "CatalogState", "CenteredPage", "Checkbox", "Cluster", "ConfirmDialog", "ConnectionStatus", "CrashState", "DataTable", "Dialog", "EmptyState", "ErrorBoundary", "ErrorState", "EventBanner", "Grid", "Icon", "IconButton", "IdText", "KeyValues", "Meter", "PageHeader", "Panel", "ProblemBanner", "ProblemState", "RadioGroup", "SegmentedControl", "Select", "Skeleton", "Spinner", "Stack", "Stat", "StateBadge", "StateRail", "StatusBadge", "Tabs", "Text", "TextArea", "TextInput", "Timeline", "TokenSwatches", "Tooltip"];

async function openCatalog(page: Page): Promise<void> {
  await page.goto("/design-system");
  await expect(page.getByRole("heading", { level: 1, name: "Design system" })).toBeVisible();
  await expect(page.locator("[data-component]").first()).toBeVisible();
}

test.describe("catalog", () => {
  test("REQ-BT-034 TM-728 catalog shows every component with visible states", async ({ page }) => {
    await openCatalog(page);
    const shown = await page.locator("[data-component]").evaluateAll((entries) => entries.map((entry) => entry.getAttribute("data-component")));
    expect(shown.filter((name) => !COMPONENTS.includes(name!)), "components missing from this test's list").toEqual([]);
    expect(COMPONENTS.filter((name) => !shown.includes(name)), "components missing from the catalog").toEqual([]);
    for (const name of COMPONENTS) {
      const entry = page.locator(`[data-component="${name}"]`);
      const states = entry.locator("[data-state-label]");
      expect(await states.count(), `${name} has states`).toBeGreaterThan(0);
      for (const state of await states.all()) await expect(state).toBeVisible();
    }
  });

  test("REQ-BT-034 TM-729 catalog at 360 px has no sideways scroll", async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 800 });
    await openCatalog(page);
    await settle(page);
    await expectNoSideScroll(page);
  });

  test("REQ-BT-034 TM-993 long text in catalog components at 360 px loses no control", async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 800 });
    await openCatalog(page);
    await settle(page);
    await expect(page.locator("[data-state-label*='long' i]").first()).toBeVisible();
    await expectNoSideScroll(page);
    expect(await clippedControls(page)).toEqual([]);
  });

  test("REQ-BT-034 TM-3787 an error in a narrow panel stacks its rows, so a value keeps the width", async ({ page }) => {
    await openCatalog(page);
    const entry = page.locator('[data-component="ErrorDetail"]');
    await entry.locator(".ds-error-row").first().waitFor();
    await entry.evaluate((root) => {
      (root as HTMLElement).style.width = "320px";
    });
    const shares = await entry.locator(".ds-error-row").evaluateAll((rows) => rows.map((row) => row.lastElementChild!.getBoundingClientRect().width / row.getBoundingClientRect().width));
    expect(Math.min(...shares)).toBeGreaterThan(0.95);
  });

  test("REQ-BT-034 TM-3788 a unit block beside a taller one keeps its name on its note, and its detail lines up with the note", async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 800 });
    await openCatalog(page);
    const units = page.locator('[data-component="UnitGrid"] .ds-unit');
    await units.first().waitFor();
    const gap = await units.nth(1).evaluate((unit) => unit.querySelector(".ds-unit-note")!.getBoundingClientRect().top - unit.querySelector(".ds-unit-name")!.getBoundingClientRect().bottom);
    expect(gap).toBeLessThan(4);
    const lined = await units.first().evaluate((unit) => unit.querySelector(".ds-unit-note")!.getBoundingClientRect().left - unit.querySelector(".ds-unit-detail span")!.getBoundingClientRect().left);
    expect(Math.abs(lined)).toBeLessThan(1);
  });

  test("REQ-BT-034 TM-730 theme switch on the catalog keeps every state readable in dark", async ({ page }) => {
    await openCatalog(page);
    const theme = page.getByRole("button", { name: /^Theme: / });
    await expect(theme).toHaveAccessibleName("Theme: system. Press to change.");
    await theme.click();
    await expect(page.getByRole("button", { name: "Theme: light. Press to change." })).toBeVisible();
    await page.getByRole("button", { name: "Theme: light. Press to change." }).click();
    await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
    await settle(page);
    const axe = await new AxeBuilder({ page }).options({ rules: { "color-contrast": { enabled: true } } }).analyze();
    expect(axe.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`)).toEqual([]);
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  });

  test("REQ-BT-034 TM-731 signed in user opens the catalog, shell stays and no session is lost", async ({ actor }) => {
    const page = await actor("olivia");
    await page.goto("/design-system");
    await expect(page.getByRole("heading", { level: 1, name: "Design system" })).toBeVisible();
    await expect(button(page, "Switch user, signed in as olivia")).toBeVisible();
    await expect(page.getByRole("navigation", { name: "Main" })).toBeVisible();
    await page.reload();
    await expect(button(page, "Switch user, signed in as olivia")).toBeVisible();
    await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Batches" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Batches" })).toBeVisible();
  });
});
