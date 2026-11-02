import { expect, type Page } from "@playwright/test";
import { api, type Batch } from "./api";
import type { User } from "./env";
import { batchPath, chooseUser, signIn } from "./ui";

export const PAGE_NAMES = ["sign in", "batches", "batch detail", "alerts", "dashboard", "design system", "not found"] as const;
export type PageName = (typeof PAGE_NAMES)[number];

/** Farm-a at Closed: every user can read it, and it can never raise an alert later. */
export function pageBatch(): Promise<Batch> {
  return api.batchAt("Closed", "farm-a", "page");
}

export async function visit(page: Page, name: PageName, user: User, batch: Batch): Promise<void> {
  if (name === "sign in") {
    await page.goto("/signin");
    await expect(chooseUser(page, user)).toBeVisible();
    return;
  }
  await signIn(page, user);
  const target: Record<Exclude<PageName, "sign in">, { path: string; heading: string }> = {
    batches: { path: "/batches", heading: "Batches" },
    "batch detail": { path: batchPath(batch), heading: batch.product },
    alerts: { path: "/alerts", heading: "Alerts" },
    dashboard: { path: "/dashboard", heading: "Dashboard" },
    "design system": { path: "/design-system", heading: "Design system" },
    "not found": { path: "/no/such/address", heading: "Page not found" },
  };
  await page.goto(target[name].path);
  await expect(page.getByRole("heading", { level: 1, name: target[name].heading })).toBeVisible();
}
