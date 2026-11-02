import { api, line as lineApi, productName } from "../support/api";
import { expect, test } from "../support/fixtures";
import { batchesTable, bodyRows, button, countRequests, expectDisabled, settle } from "../support/ui";

const NEW_BATCH_REFUSED = "Creating a batch needs the Operator or Admin role.";

test.describe("batches screen", () => {
  test("REQ-BT-004 TM-482 olivia creates a batch and it opens at Created", async ({ actor }) => {
    const page = await actor("olivia");
    const product = productName("create");
    await button(page, "New batch").click();
    const dialog = page.getByRole("dialog", { name: "New batch" });
    await dialog.getByLabel("Product").fill(product);
    await dialog.getByLabel("Farm").selectOption("farm-b");
    await button(page, "Create batch").click();
    await expect(page.getByRole("heading", { level: 1, name: product })).toBeVisible();
    await expect(page).toHaveURL(/\/batches\/farm-b\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("img", { name: "Step 1 of 6" })).toBeVisible();
    await page.reload();
    await expect(page.getByRole("heading", { level: 1, name: product })).toBeVisible();
    await expect(page.getByRole("img", { name: "Step 1 of 6" })).toBeVisible();
  });

  test("REQ-BT-004 TM-483 empty product is refused beside the field and no batch is made", async ({ actor }) => {
    const page = await actor("olivia");
    const creates = countRequests(page, "POST", /\/api\/batches$/);
    await button(page, "New batch").click();
    const dialog = page.getByRole("dialog", { name: "New batch" });
    await button(page, "Create batch").click();
    await expect(dialog.getByRole("alert").filter({ hasText: "Enter the product name." })).toBeVisible();
    await expect(dialog.getByLabel("Product")).toHaveAttribute("aria-invalid", "true");
    await dialog.getByLabel("Product").fill("     ");
    await button(page, "Create batch").click();
    await expect(dialog.getByRole("alert").filter({ hasText: "Enter the product name." })).toBeVisible();
    await expect(dialog).toBeVisible();
    expect(creates.count).toBe(0);
  });

  test("REQ-BT-004 TM-484 olivia chooses from farm-a and farm-b only", async ({ actor }) => {
    const page = await actor("olivia");
    await button(page, "New batch").click();
    const options = page.getByRole("dialog", { name: "New batch" }).getByLabel("Farm").locator("option");
    await expect(options).toHaveText(["farm-a", "farm-b"]);
  });

  test("REQ-BT-004 TM-485 markup in a product name shows as text and no script runs", async ({ actor }) => {
    const page = await actor("olivia");
    const product = `<img src=x onerror="window.__xss=1"><script>window.__xss=2</script> ${productName("markup")}`;
    await button(page, "New batch").click();
    await page.getByRole("dialog", { name: "New batch" }).getByLabel("Product").fill(product);
    await button(page, "Create batch").click();
    await expect(page.getByRole("heading", { level: 1, name: product })).toHaveText(product);
    await page.goto("/batches");
    await expect(batchesTable(page).getByRole("link", { name: product })).toHaveText(product);
    await expect(page.locator("main img")).toHaveCount(0);
    expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss)).toBeUndefined();
  });

  test("REQ-BT-016 TM-486 viewer lists farm-a only and New batch is disabled with the reason", async ({ actor }) => {
    const own = await api.batchAt("Created", "farm-a", "viewer own");
    const other = await api.batchAt("Created", "farm-b", "viewer other");
    const page = await actor("viewer");
    await expect(batchesTable(page).getByRole("link", { name: own.product })).toBeVisible();
    await expect(batchesTable(page).getByRole("link", { name: other.product })).toHaveCount(0);
    await expect(page.locator('td[data-label="Farm"]').filter({ hasNotText: /^farm-a$/ })).toHaveCount(0);
    await expect(page.getByLabel("Farm", { exact: true }).locator("option")).toHaveText(["All my farms", "farm-a"]);
    await expectDisabled(button(page, "New batch"), NEW_BATCH_REFUSED);
  });

  test("REQ-BT-016 TM-487 viewer at a farm-b address gets the 403 page with a way back", async ({ actor, guard }) => {
    const other = await api.batchAt("Created", "farm-b", "viewer refused");
    const page = await actor("viewer");
    guard.expectResponse("GET", new RegExp(`/api/batches/farm-b/${other.id}$`), 403);
    await page.goto(`/batches/farm-b/${other.id}`);
    await expect(page.getByRole("alert").filter({ hasText: "You do not have access" })).toBeVisible();
    await expect(page.getByRole("heading", { level: 1, name: other.product })).toHaveCount(0);
    await expect(button(page, "Switch user")).toHaveCount(0);
    await button(page, "Back to batches").click();
    await expect(page.getByRole("heading", { level: 1, name: "Batches" })).toBeVisible();
  });

  test("REQ-BT-016 TM-488 olivia filters by farm-b and sees only farm-b rows", async ({ actor }) => {
    const a = await api.batchAt("Created", "farm-a", "filter a");
    const b = await api.batchAt("Created", "farm-b", "filter b");
    const page = await actor("olivia");
    await expect(batchesTable(page).getByRole("link", { name: a.product })).toBeVisible();
    await page.getByLabel("Farm", { exact: true }).selectOption("farm-b");
    await expect(batchesTable(page).getByRole("link", { name: b.product })).toBeVisible();
    await expect(batchesTable(page).getByRole("link", { name: a.product })).toHaveCount(0);
    await expect(page.locator('td[data-label="Farm"]').filter({ hasNotText: /^farm-b$/ })).toHaveCount(0);
    await page.reload();
    await expect(page.getByLabel("Farm", { exact: true })).toHaveValue("farm-b");
    await expect(batchesTable(page).getByRole("link", { name: b.product })).toBeVisible();
    await expect(batchesTable(page).getByRole("link", { name: a.product })).toHaveCount(0);
  });

  test("REQ-BT-016 TM-489 olivia at a farm-c address gets the 403 page with a way back", async ({ actor, guard }) => {
    const other = await api.create("ada", "farm-c", productName("olivia refused"));
    const page = await actor("olivia");
    guard.expectResponse("GET", new RegExp(`/api/batches/farm-c/${other.id}$`), 403);
    await page.goto(`/batches/farm-c/${other.id}`);
    await expect(page.getByRole("alert").filter({ hasText: "You do not have access" })).toBeVisible();
    await button(page, "Back to batches").click();
    await expect(page.getByRole("heading", { level: 1, name: "Batches" })).toBeVisible();
  });

  test("REQ-BT-036 TM-490 ian lists all three farms and New batch is disabled with the reason", async ({ actor }) => {
    const rows = [await api.create("ada", "farm-a", productName("ian a")), await api.create("ada", "farm-b", productName("ian b")), await api.create("ada", "farm-c", productName("ian c"))];
    const page = await actor("ian");
    for (const batch of rows) {
      const row = bodyRows(batchesTable(page)).filter({ hasText: batch.product });
      await expect(row.locator('td[data-label="Farm"]')).toHaveText(batch.farmId);
    }
    await expect(page.getByLabel("Farm", { exact: true }).locator("option")).toHaveText(["All my farms", "farm-a", "farm-b", "farm-c"]);
    await expectDisabled(button(page, "New batch"), NEW_BATCH_REFUSED);
  });

  test("REQ-BT-036 TM-491 filters with no match, Clear filters returns the rows", async ({ actor }) => {
    const batch = await api.batchAt("Created", "farm-b", "no match");
    const page = await actor("olivia");
    await expect(batchesTable(page).getByRole("link", { name: batch.product })).toBeVisible();
    await page.getByLabel("State").selectOption("Closed");
    await page.getByLabel("Attention").selectOption("blocked");
    await expect(page.getByText("No batches match these filters")).toBeVisible();
    await button(page, "Clear filters").click();
    await expect(batchesTable(page).getByRole("link", { name: batch.product })).toBeVisible();
    await expect(page.getByLabel("State")).toHaveValue("");
    await expect(page.getByLabel("Attention")).toHaveValue("");
  });

  test("REQ-BT-036 TM-493 with the line running, rows hold under the pointer and resume when it leaves", async ({ actor, line }) => {
    await api.batchAt("Created", "farm-b", "hold");
    const page = await actor("ada");
    await settle(page);
    await line.start(2);
    await batchesTable(page).hover();
    await expect(page.getByText("List held while the pointer or focus is inside it.")).toBeVisible();
    const held = await page.locator('td[data-label="Product"]').allInnerTexts();

    let newest = "";
    await expect
      .poll(
        async () => {
          newest = (await api.list("ada", 1))[0]!.product;
          return held.includes(newest);
        },
        { timeout: 20_000, message: "the line never made a newer batch" },
      )
      .toBe(false);
    expect(await page.locator('td[data-label="Product"]').allInnerTexts()).toEqual(held);

    await page.mouse.move(0, 0);
    await expect(page.getByText("List held while the pointer or focus is inside it.")).toHaveCount(0);
    await expect(batchesTable(page).getByRole("link", { name: newest, exact: true })).toBeVisible();
    await line.stop();
    await lineApi.quiet();
  });
});
