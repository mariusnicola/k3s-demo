import { AxeBuilder } from "@axe-core/playwright";
import type { Page } from "@playwright/test";
import { api } from "../support/api";
import { HEIGHT, WIDTHS, type User } from "../support/env";
import { expect, test } from "../support/fixtures";
import { PAGE_NAMES, pageBatch, visit, type PageName } from "../support/pages";
import { batchesTable, button, chooseUser, clippedControls, expectEnabled, expectNoSideScroll, openBatch, panel, settle } from "../support/ui";

/** Admin reaches every page, so each is checked in its full form. */
const FULL_USER: User = "ada";

async function axeViolations(page: Page): Promise<string[]> {
  const result = await new AxeBuilder({ page }).options({ rules: { "color-contrast": { enabled: true } } }).analyze();
  return result.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => `${n.target.join(" ")} ${n.failureSummary?.split("\n").slice(1).join(" ").trim() ?? ""}`).join(" | ")}`);
}

/** Presses Tab until the named control holds focus, and checks the focus ring is drawn on the way. */
async function tabTo(page: Page, name: string | RegExp): Promise<void> {
  for (let press = 0; press < 80; press += 1) {
    await page.keyboard.press("Tab");
    const focused = await page.evaluate(() => {
      const el = document.activeElement as HTMLElement | null;
      if (!el || el === document.body) return null;
      const style = getComputedStyle(el);
      const ring = (style.outlineStyle !== "none" && parseFloat(style.outlineWidth) > 0) || style.boxShadow !== "none";
      return { name: (el.getAttribute("aria-label") ?? el.textContent ?? "").trim(), ring };
    });
    if (!focused) continue;
    expect(focused.ring, `focus ring on "${focused.name}"`).toBe(true);
    if (typeof name === "string" ? focused.name === name : name.test(focused.name)) return;
  }
  throw new Error(`Tab never reached ${name}`);
}

test.describe("accessibility and layout", () => {
  for (const name of PAGE_NAMES) {
    for (const width of WIDTHS) {
      for (const [row, scheme] of [["TM-741", "light"], ["TM-742", "dark"]] as const) {
        test(`REQ-BT-035 ${row} real axe, ${scheme} theme, ${name} at ${width} px`, async ({ page }) => {
          const batch = await pageBatch();
          await page.setViewportSize({ width, height: HEIGHT });
          await page.emulateMedia({ colorScheme: scheme });
          await visit(page, name, FULL_USER, batch);
          await settle(page);
          await expect(page.locator("html")).toHaveAttribute("data-theme", scheme);
          expect(await axeViolations(page)).toEqual([]);
        });
      }
    }
  }

  const layout = async (page: Page, name: PageName, width: number) => {
    const batch = await pageBatch();
    await page.setViewportSize({ width, height: HEIGHT });
    await visit(page, name, FULL_USER, batch);
    await settle(page);
    await expectNoSideScroll(page);
    expect(await clippedControls(page)).toEqual([]);
  };

  for (const name of PAGE_NAMES) {
    test(`REQ-BT-035 TM-743 ${name} at 1440 px has no sideways scroll and no clipped control`, async ({ page }) => {
      await layout(page, name, 1440);
    });
    test(`REQ-BT-035 TM-744 ${name} at 360 px has no sideways scroll and every action in reach`, async ({ page }) => {
      await layout(page, name, 360);
    });
  }

  test("REQ-BT-035 TM-744 a whole batch walk works at 360 px", async ({ actor }) => {
    const batch = await api.batchAt("Inspected");
    const page = await actor("olivia", { viewport: { width: 360, height: 800 } });
    await openBatch(page, batch);
    await button(page, "Move to Shipped").click();
    await expectEnabled(button(page, "Move to Closed"));
    await expectNoSideScroll(page);
    await button(page, "Move to Closed").click();
    await expectNoSideScroll(page);
    await button(page, "Close batch").click();
    await expect(panel(page, "Next step").getByText("Closed, final")).toBeVisible();
  });

  test("REQ-BT-035 TM-745 tables stack into label and value rows at 720 px and sit as tables above", async ({ actor }) => {
    await api.batchAt("Created");
    const page = await actor("olivia", { viewport: { width: 720, height: 800 } });
    const cell = batchesTable(page).locator('td[data-label="Farm"]').first();
    await expect(cell).toBeVisible();
    expect(await cell.evaluate((el) => getComputedStyle(el).display)).toBe("grid");
    expect(await cell.evaluate((el) => getComputedStyle(el, "::before").content)).toBe('"Farm"');
    await page.setViewportSize({ width: 721, height: 800 });
    await expect.poll(() => cell.evaluate((el) => getComputedStyle(el).display)).toBe("table-cell");
  });

  test("REQ-BT-035 TM-746 controls are 44 px at 720 px, 40 px above, 44 px on touch", async ({ actor }) => {
    const height = (page: Page) => button(page, "New batch").evaluate((el) => el.getBoundingClientRect().height);
    const wide = await actor("olivia", { viewport: { width: 1440, height: 800 } });
    expect(await height(wide)).toBe(40);
    await wide.setViewportSize({ width: 720, height: 800 });
    await expect.poll(() => height(wide)).toBe(44);
    const touch = await actor("olivia", { viewport: { width: 1024, height: 800 }, hasTouch: true, isMobile: true });
    expect(await height(touch)).toBe(44);
  });

  test("REQ-BT-035 TM-747 olivia creates and moves a batch with the keyboard alone", async ({ page }) => {
    await page.goto("/signin");
    await tabTo(page, /^Continue as olivia/);
    await page.keyboard.press("Enter");
    await expect(page.getByRole("heading", { level: 1, name: "Dashboard" })).toBeVisible();
    await tabTo(page, "Batches");
    await page.keyboard.press("Enter");
    await expect(page.getByRole("heading", { level: 1, name: "Batches" })).toBeVisible();
    await tabTo(page, "New batch");
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "New batch" });
    await expect(dialog.getByLabel("Product")).toBeFocused();
    const product = `e2e keyboard ${Date.now().toString(36)}`;
    await page.keyboard.type(product);
    await page.keyboard.press("Enter");
    await expect(page.getByRole("heading", { level: 1, name: product })).toBeVisible();
    await expectEnabled(button(page, "Move to Dose 1"));
    await tabTo(page, "Move to Dose 1");
    await page.keyboard.press("Enter");
    await expectEnabled(button(page, "Move to Dose 2"));
  });

  test("REQ-BT-035 TM-747 ian records an inspection and olivia closes with the keyboard alone", async ({ actor }) => {
    const batch = await api.batchAt("Dose2");
    const ian = await actor("ian");
    await openBatch(ian, batch);
    await tabTo(ian, "Record inspection");
    await ian.keyboard.press("Enter");
    await expect(ian.getByRole("radio", { name: /^Pass/ })).toBeFocused();
    await tabTo(ian, "Record pass");
    await ian.keyboard.press("Enter");
    await expect(ian.getByRole("dialog")).toHaveCount(0);

    await api.settled(batch);
    await api.advance("olivia", batch, "Shipped");
    await api.settled(batch);
    const olivia = await actor("olivia");
    await openBatch(olivia, batch);
    await tabTo(olivia, "Move to Closed");
    await olivia.keyboard.press("Enter");
    const dialog = olivia.getByRole("dialog", { name: "Close this batch" });
    await expect(dialog.getByRole("button", { name: "Cancel" })).toBeFocused();
    await olivia.keyboard.press("Tab");
    await expect(dialog.getByRole("button", { name: "Close batch" })).toBeFocused();
    await olivia.keyboard.press("Enter");
    await expect(panel(olivia, "Next step").getByText("This batch is complete. No further step exists.")).toBeVisible();
  });

  test("REQ-BT-035 TM-747 ada sets fail next and replays with the keyboard alone", async ({ actor }) => {
    const batch = await api.batchAt("Created");
    const page = await actor("ada");
    await openBatch(page, batch);
    await tabTo(page, "Fail next delivery");
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Fail the next delivery" });
    await expect(dialog.getByRole("button", { name: "Cancel" })).toBeFocused();
    await page.keyboard.press("Tab");
    await page.keyboard.press("Enter");
    await expect(page.getByText("Next delivery set to fail")).toBeVisible();
    await page.locator("body").evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
    await tabTo(page, "Move to Dose 1");
    await page.keyboard.press("Enter");
    await expect(button(page, "Replay event")).toBeVisible();
    await page.locator("body").evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
    await tabTo(page, "Replay event");
    await page.keyboard.press("Enter");
    await expectEnabled(button(page, "Move to Dose 2"));
  });

  test("REQ-BT-035 TM-748 reduced motion set in the browser gives no dot tween or pulse", async ({ browser }) => {
    const context = await browser.newContext({ baseURL: test.info().project.use.baseURL, reducedMotion: "reduce", viewport: { width: 1440, height: 900 } });
    const page = await context.newPage();
    await page.goto("/signin");
    await chooseUser(page, "ada").click();
    await expect(page.getByRole("heading", { level: 1, name: "Dashboard" })).toBeVisible();
    await page.goto("/dashboard");
    await expect(page.getByRole("heading", { level: 1, name: "Dashboard" })).toBeVisible();
    await settle(page);
    await expect(page.locator("figure[data-reduced]")).toBeVisible();

    await page.evaluate(() => {
      const win = window as unknown as { __at: string[] };
      win.__at = [];
      const sample = () => {
        for (const dot of document.querySelectorAll(".ds-dot[visibility=visible]")) {
          const at = dot.getAttribute("transform") ?? "";
          if (!win.__at.includes(at)) win.__at.push(at);
        }
        requestAnimationFrame(sample);
      };
      requestAnimationFrame(sample);
    });
    const batch = await api.batchAt("Created");
    await api.advance("olivia", batch, "Dose1");
    await api.settled(batch);
    await expect.poll(() => page.evaluate(() => (window as unknown as { __at: string[] }).__at.length), { message: "a dot must appear" }).toBeGreaterThan(0);

    // A dot lands on a block's anchor: a station, or the pod a hop names.
    const stations = await page.locator("g[data-anchor]").evaluateAll((nodes) => nodes.map((node) => node.getAttribute("data-anchor")!.split(" ").map(Number).join(" ")));
    const seen = await page.evaluate(() => (window as unknown as { __at: string[] }).__at.map((t) => t.match(/-?[\d.]+/g)!.map(Number).join(" ")));
    expect(seen.filter((at) => !stations.includes(at)), "dots sit only on stations, with no tween").toEqual([]);
    const halo = await page.locator(".ds-dot-halo").first().evaluate((el) => getComputedStyle(el).animationName);
    expect(halo).toBe("none");
    await context.close();
  });
});
