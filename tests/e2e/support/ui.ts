import { expect, type Locator, type Page } from "@playwright/test";
import type { Batch } from "./api";
import type { User } from "./env";

export const ROLE_LABEL: Record<User, string> = { olivia: "Operator", ian: "Inspector", ada: "Admin", viewer: "Read only" };

export function chooseUser(page: Page, user: User): Locator {
  return page.getByRole("button", { name: `Continue as ${user} (${ROLE_LABEL[user]})` });
}

/** Signs in on the real page, which lands on the dashboard, and moves to Batches, where most tests start. */
export async function signIn(page: Page, user: User): Promise<void> {
  await page.goto("/signin");
  await chooseUser(page, user).click();
  await expect(page.getByRole("heading", { level: 1, name: "Dashboard" })).toBeVisible();
  await page.goto("/batches");
  await expect(page.getByRole("heading", { level: 1, name: "Batches" })).toBeVisible();
}

export function batchPath(batch: Pick<Batch, "farmId" | "id">): string {
  return `/batches/${batch.farmId}/${batch.id}`;
}

export async function openBatch(page: Page, batch: Batch): Promise<void> {
  await page.goto(batchPath(batch));
  await expect(page.getByRole("heading", { level: 1, name: batch.product })).toBeVisible();
}

export function button(page: Page, name: string | RegExp): Locator {
  return page.getByRole("button", { name, exact: typeof name === "string" });
}

export async function expectDisabled(control: Locator, reason: string | RegExp): Promise<void> {
  await expect(control).toHaveAttribute("aria-disabled", "true");
  await expect(control).toHaveAccessibleDescription(reason);
}

export async function expectEnabled(control: Locator): Promise<void> {
  await expect(control).toBeVisible();
  await expect(control).not.toHaveAttribute("aria-disabled", "true");
}

export function panel(page: Page, name: string): Locator {
  return page.getByRole("region", { name, exact: true });
}

export function detail(page: Page, label: string): Locator {
  return panel(page, "Details").locator("dt", { hasText: new RegExp(`^${label}$`) }).locator("+ dd");
}

export function bodyRows(table: Locator): Locator {
  return table.locator("tbody").getByRole("row");
}

export function recordsTable(page: Page): Locator {
  return page.getByRole("table", { name: "Compliance records" });
}

export function batchesTable(page: Page): Locator {
  return page.getByRole("table", { name: "Batches" });
}

export function timelineSteps(page: Page): Locator {
  return page.getByRole("tabpanel").getByRole("listitem");
}

/** Counts the requests of one kind a page sends. */
export function countRequests(page: Page, method: string, url: RegExp): { count: number } {
  const seen = { count: 0 };
  page.on("request", (request) => {
    if (request.method() === method && url.test(request.url())) seen.count += 1;
  });
  return seen;
}

interface BlockedPress {
  reason: string;
}

/**
 * An event lives for under a second, too short to catch by polling. This watches the page and
 * presses the named control the moment it renders blocked, as a person pressing at once would.
 */
export async function pressWhileBlocked(page: Page, name: string): Promise<void> {
  await page.evaluate((target) => {
    const win = window as unknown as { __blocked?: BlockedPress };
    const check = () => {
      if (win.__blocked) return;
      for (const control of document.querySelectorAll("button")) {
        if (control.textContent?.trim() !== target || control.getAttribute("aria-disabled") !== "true") continue;
        const id = control.getAttribute("aria-describedby");
        const reason = id ? (document.getElementById(id)?.textContent ?? "") : "";
        if (!reason) continue;
        control.click();
        win.__blocked = { reason };
      }
    };
    new MutationObserver(check).observe(document.body, { subtree: true, childList: true, attributes: true, characterData: true });
    check();
  }, name);
}

export function blockedPress(page: Page): Promise<BlockedPress | undefined> {
  return page.evaluate(() => (window as unknown as { __blocked?: BlockedPress }).__blocked);
}

async function stillSettling(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const shown = (el: Element) => !el.closest(".ds-catalog-state") && el.getClientRects().length > 0;
    return [...document.querySelectorAll("[role=status], [aria-busy=true], .ds-skeleton")]
      .filter(shown)
      .map((el) => el.getAttribute("aria-label") ?? el.textContent ?? "")
      .filter((text) => /loading|waiting|reading|restoring|connecting/i.test(text));
  });
}

/** Done when no loader or connecting status is left. A page that never settles fails here. */
export async function settle(page: Page): Promise<void> {
  await expect.poll(() => stillSettling(page), { timeout: 30_000, message: "page never settled" }).toEqual([]);
  await page.waitForLoadState("networkidle");
}

/** Every error state on screen. The catalog shows them on purpose as samples, so its sample rows do not count. */
export async function visibleErrors(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const selector = "[role=alert], .ds-error-state, .ds-banner[data-tone=danger], [data-status=lost]";
    return [...document.querySelectorAll(selector)]
      .filter((el) => !el.closest(".ds-catalog-state") && el.getClientRects().length > 0)
      .map((el) => (el.textContent ?? "").trim());
  });
}

export async function expectNoSideScroll(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, "sideways scroll in px").toBeLessThanOrEqual(0);
}

/** Lists controls that stick out of the window or cut their own label. */
export async function clippedControls(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const width = document.documentElement.clientWidth;
    return [...document.querySelectorAll("button, a, select, input, textarea")]
      .filter((el) => el.getClientRects().length > 0 && !el.classList.contains("ds-skip-link"))
      .filter((el) => {
        const box = el.getBoundingClientRect();
        return box.left < 0 || box.right > width + 0.5 || el.scrollWidth > el.clientWidth + 1;
      })
      .map((el) => `${el.tagName} ${(el.textContent ?? "").trim().slice(0, 40)}`);
  });
}
