import { api } from "../support/api";
import { expect, test } from "../support/fixtures";
import { button, batchPath, chooseUser, countRequests, settle, signIn } from "../support/ui";

test.describe("sign in and session", () => {
  test("REQ-BT-015 TM-441 anonymous at / lands on sign in with next", async ({ page }) => {
    await page.goto("/");
    await expect(page).toHaveURL(/\/signin\?next=%2F$/);
    await expect(page.getByRole("heading", { level: 1, name: "Sign in to Batch Tracker" })).toBeVisible();
    await expect(chooseUser(page, "olivia")).toBeVisible();
  });

  test("REQ-BT-015 TM-442 anonymous at /dashboard signs in as olivia and gets her dashboard with no loop", async ({ page }) => {
    await page.goto("/dashboard");
    await expect(page).toHaveURL(/\/signin\?next=%2Fdashboard$/);
    await chooseUser(page, "olivia").click();
    await expect(page.getByRole("region", { name: "Batches by step" })).toBeVisible();
    await expect(page).toHaveURL(/\/dashboard$/);
    await settle(page);
    await expect(page).toHaveURL(/\/dashboard$/);
  });

  test("REQ-BT-015 TM-443 anonymous opens the catalog without sign in", async ({ page }) => {
    await page.goto("/design-system");
    await expect(page).toHaveURL(/\/design-system$/);
    await expect(page.getByRole("heading", { level: 1, name: "Design system" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Sign in" })).toBeVisible();
  });

  test("REQ-BT-036 TM-444 anonymous at an unknown address signs in first, then gets the way back", async ({ page }) => {
    await page.goto("/no/such/address?x=1");
    await expect(page).toHaveURL(/\/signin\?next=%2Fno%2Fsuch%2Faddress%3Fx%3D1$/);
    await chooseUser(page, "viewer").click();
    await expect(page.getByRole("heading", { level: 1, name: "Page not found" })).toBeVisible();
    await expect(page).toHaveURL(/\/no\/such\/address\?x=1$/);
    await expect(page.getByRole("link", { name: "all batches" })).toBeVisible();
  });

  test("REQ-BT-015 TM-445 switch to viewer on a /batches?farm=farm-b address drops the filter with no 403", async ({ page }) => {
    await signIn(page, "olivia");
    await page.goto("/batches?farm=farm-b");
    await expect(page.getByLabel("Farm", { exact: true })).toHaveValue("farm-b");
    const filtered = countRequests(page, "GET", /\/api\/batches\?.*farmId=farm-b/);
    await button(page, "Switch user, signed in as olivia").click();
    await chooseUser(page, "viewer").click();
    await expect(page.getByRole("heading", { level: 1, name: "Dashboard" })).toBeVisible();
    await page.goto("/batches");
    await expect(page.getByLabel("Farm", { exact: true })).toHaveValue("");
    await settle(page);
    expect(filtered.count, "farm-b must never reach the API as viewer").toBe(0);
  });

  test("REQ-BT-015 TM-446 switch user ends the session, a protected address then needs a new sign in", async ({ page }) => {
    await signIn(page, "ada");
    await button(page, "Switch user, signed in as ada").click();
    await expect(page).toHaveURL(/\/signin/);
    await expect(chooseUser(page, "ada")).toBeVisible();
    await page.goto("/alerts");
    await expect(page).toHaveURL(/\/signin\?next=%2Falerts$/);
    await page.reload();
    await expect(chooseUser(page, "ada")).toBeVisible();
  });

  test("REQ-BT-055 olivia sees her dashboard, and ada sees the cluster after switching user", async ({ actor }) => {
    const page = await actor("olivia");
    await page.getByRole("link", { name: "Dashboard" }).click();
    await expect(page.getByRole("region", { name: "Batches by step" })).toBeVisible();
    await expect(page.getByText("needs an admin")).toHaveCount(0);
    await button(page, "Switch user, signed in as olivia").click();
    await expect(chooseUser(page, "ada")).toBeVisible();
    await chooseUser(page, "ada").click();
    await expect(page).toHaveURL(/\/dashboard$/);
    await expect(page.getByRole("heading", { level: 1, name: "Dashboard" })).toBeVisible();
    await expect(page.getByRole("region", { name: "Architecture" })).toBeVisible();
  });

  test("REQ-BT-055 TM-697 ian opens Dashboard and gets the inspection view", async ({ actor }) => {
    const page = await actor("ian");
    await page.goto("/dashboard");
    await expect(page.getByRole("region", { name: "Batches by step" })).toBeVisible();
    await expect(page.getByText("Inspection across all farms.")).toBeVisible();
    await expect(page.getByRole("region", { name: "Production line" })).toHaveCount(0);
  });

  test("REQ-BT-055 TM-613 viewer opens Dashboard and no line socket opens", async ({ page }) => {
    const sockets: string[] = [];
    page.on("websocket", (socket) => sockets.push(socket.url()));
    await page.goto("/dashboard");
    await chooseUser(page, "viewer").click();
    await expect(page.getByRole("region", { name: "Batches by step" })).toBeVisible();
    await settle(page);
    expect(sockets.length, "the cluster work banner keeps one socket open").toBeGreaterThan(0);
    expect(sockets.map((url) => new URL(url).pathname).filter((path) => path !== "/ws" && path !== "/ws/hops")).toEqual([]);
  });

  test("REQ-BT-036 TM-757 unknown address signed in shows a page with a way back", async ({ actor }) => {
    const page = await actor("olivia");
    await page.goto("/no/such/address");
    await expect(page.getByRole("heading", { level: 1, name: "Page not found" })).toBeVisible();
    await expect(page.getByText("Nothing lives at this address")).toBeVisible();
    await expect(page.getByRole("link", { name: "all batches" })).toBeVisible();
    await expect(page.getByRole("navigation", { name: "Main" })).toBeVisible();
  });

  test("REQ-BT-036 TM-758 way back from not found lands on Batches", async ({ actor }) => {
    const page = await actor("olivia");
    await page.goto("/no/such/address");
    await page.getByRole("link", { name: "all batches" }).click();
    await expect(page).toHaveURL(/\/batches$/);
    await expect(page.getByRole("heading", { level: 1, name: "Batches" })).toBeVisible();
  });

  test("REQ-BT-015 TM-695 viewer on a farm-a batch has every action disabled with its reason, no write sent", async ({ actor }) => {
    const created = await api.batchAt("Created", "farm-a", "viewer read");
    const dose2 = await api.batchAt("Dose2", "farm-a", "viewer dose2");
    const page = await actor("viewer");
    const writes = countRequests(page, "POST", /\/api\/batches/);

    await page.goto(batchPath(created));
    const move = button(page, "Move to Dose 1");
    await expect(move).toHaveAttribute("aria-disabled", "true");
    await expect(move).toHaveAccessibleDescription("Moving a batch needs the Operator or Admin role.");
    await move.click({ force: true });
    await expect(page.getByRole("region", { name: "Admin" })).toHaveCount(0);

    await page.goto(batchPath(dose2));
    const inspect = button(page, "Record inspection");
    await expect(inspect).toHaveAttribute("aria-disabled", "true");
    await expect(inspect).toHaveAccessibleDescription("Inspection needs the Inspector or Admin role.");
    await inspect.click({ force: true });
    await expect(page.getByRole("dialog")).toHaveCount(0);

    await page.goto("/batches");
    await expect(button(page, "New batch")).toHaveAttribute("aria-disabled", "true");
    await expect(button(page, "New batch")).toHaveAccessibleDescription("Creating a batch needs the Operator or Admin role.");
    await button(page, "New batch").click({ force: true });
    await expect(page.getByRole("dialog")).toHaveCount(0);
    expect(writes.count).toBe(0);
  });
});
