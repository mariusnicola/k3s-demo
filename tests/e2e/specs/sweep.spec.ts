import type { Page } from "@playwright/test";
import { api, type Batch } from "../support/api";
import { USERS, HEIGHT, WIDTHS, type User } from "../support/env";
import { expect, test } from "../support/fixtures";
import { batchPath, chooseUser, expectNoSideScroll, settle, signIn, visibleErrors } from "../support/ui";

type Actor = "anonymous" | User;
const ACTORS: Actor[] = ["anonymous", ...USERS];

interface SweptPage {
  name: string;
  path: (batch: Batch) => string;
  heading: (batch: Batch) => string;
  open?: boolean;
}

const PAGES: SweptPage[] = [
  { name: "sign in", path: () => "/signin", heading: () => "Sign in to Batch Tracker", open: true },
  { name: "batches", path: () => "/batches", heading: () => "Batches" },
  { name: "batch detail", path: (batch) => batchPath(batch), heading: (batch) => batch.product },
  { name: "alerts", path: () => "/alerts", heading: () => "Alerts" },
  { name: "dashboard", path: () => "/dashboard", heading: () => "Dashboard" },
  { name: "design system", path: () => "/design-system", heading: () => "Design system", open: true },
  { name: "not found", path: () => "/not-a-page", heading: () => "Page not found" },
];

async function expectClean(page: Page): Promise<void> {
  await settle(page);
  expect(await visibleErrors(page)).toEqual([]);
  await expectNoSideScroll(page);
}

// Row order in the matrix: page, then actor, then width, from TM-994. The console half is the guard every test carries.
test.describe("page sweep", () => {
  PAGES.forEach((swept, pageIndex) => {
    ACTORS.forEach((actor, actorIndex) => {
      WIDTHS.forEach((width, widthIndex) => {
        const row = 994 + pageIndex * ACTORS.length * WIDTHS.length + actorIndex * WIDTHS.length + widthIndex;
        test(`REQ-BT-040 TM-${row} sweep ${swept.name} as ${actor} at ${width} px shows no error state and logs nothing`, async ({ page }) => {
          const batch = await api.batchAt("Closed", "farm-a", "sweep");
          await page.setViewportSize({ width, height: HEIGHT });

          if (actor === "anonymous") {
            await page.goto(swept.path(batch));
            if (swept.open) await expect(page.getByRole("heading", { level: 1, name: swept.heading(batch) })).toBeVisible();
            else {
              await expect(page).toHaveURL(/\/signin\?next=/);
              await expect(chooseUser(page, "olivia")).toBeVisible();
            }
            await expectClean(page);
            return;
          }

          if (swept.name === "sign in") {
            await signIn(page, actor);
            await expectClean(page);
            return;
          }

          await signIn(page, actor);
          await page.goto(swept.path(batch));
          await expect(page.getByRole("heading", { level: 1, name: swept.heading(batch) })).toBeVisible();
          await expectClean(page);
        });
      });
    });
  });
});
