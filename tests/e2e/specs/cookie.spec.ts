import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import type { Browser, BrowserContext, Page, WebSocket } from "@playwright/test";
import { api, productName } from "../support/api";
import { API, BASE } from "../support/env";
import { chromium, expect, test } from "../support/fixtures";
import { batchPath, button, chooseUser, openBatch, settle, signIn } from "../support/ui";

async function sessionCookies(context: BrowserContext) {
  return (await context.cookies(BASE)).filter((cookie) => cookie.httpOnly);
}

function cookieHeader(cookies: { name: string; value: string }[]): string {
  return cookies.map((cookie) => `${cookie.name}=${cookie.value}`).join("; ");
}

test.describe("session cookie", () => {
  test("REQ-BT-047 TM-1935 session holds over reload and a second tab, and page script cannot read it", async ({ page, context }) => {
    await signIn(page, "olivia");
    expect((await sessionCookies(context)).length, "an HttpOnly session cookie exists").toBeGreaterThan(0);
    expect(await page.evaluate(() => document.cookie)).toBe("");
    expect(await page.evaluate(() => JSON.stringify({ ...sessionStorage, ...localStorage })), "no token in browser storage").not.toMatch(/eyJ/);

    await page.reload();
    await expect(button(page, "Switch user, signed in as olivia")).toBeVisible();

    const second = await context.newPage();
    await second.goto("/alerts");
    await expect(second.getByRole("heading", { level: 1, name: "Alerts" })).toBeVisible();
    await expect(button(second, "Switch user, signed in as olivia")).toBeVisible();
  });

  test("REQ-BT-047 TM-1937 no request or socket URL holds a token on any journey", async ({ page }) => {
    const urls: string[] = [];
    page.on("request", (request) => urls.push(request.url()));
    page.on("websocket", (socket) => urls.push(socket.url()));
    const batch = await api.batchAt("Created", "farm-b", "no token");
    await signIn(page, "ada");
    await settle(page);
    await openBatch(page, batch);
    await settle(page);
    await button(page, "Move to Dose 1").click();
    await expect(button(page, "Move to Dose 2")).toBeVisible();
    for (const path of ["/alerts", "/dashboard", "/logs", "/design-system"]) {
      await page.goto(path);
      await settle(page);
    }
    expect(urls.length).toBeGreaterThan(5);
    expect(urls.filter((url) => /token=|eyJ[\w-]+\./.test(url))).toEqual([]);
  });

  test("REQ-BT-047 TM-1938 switch user clears the cookie and closes the socket, a copied cookie lives until it expires", async ({ page, context }) => {
    const hopSockets: WebSocket[] = [];
    page.on("websocket", (socket) => {
      if (new URL(socket.url()).pathname === "/ws/hops") hopSockets.push(socket);
    });
    await signIn(page, "ada");
    await expect(page.getByText("Live updates: live")).toBeVisible();
    expect(hopSockets.length, "the signed-in page holds a hop socket").toBeGreaterThan(0);
    const before = await sessionCookies(context);
    expect(before.length, "an HttpOnly session cookie exists").toBeGreaterThan(0);

    await button(page, "Switch user, signed in as ada").click();
    await expect(chooseUser(page, "ada")).toBeVisible();
    expect(await sessionCookies(context)).toEqual([]);
    // A socket of the page before the last navigation never reports its close, so only the current one can.
    await expect.poll(() => hopSockets.at(-1)!.isClosed(), { message: "the client closes its socket" }).toBe(true);

    const copied = await fetch(`${API}/me`, { headers: { cookie: cookieHeader(before) } });
    expect(copied.status, "no deny list, so a copied cookie works until it expires").toBe(200);
  });

  test.describe("page from another origin", () => {
    let hostile: Server;
    let browser: Browser;

    test.beforeAll(async () => {
      hostile = createServer((_request, response) => {
        response.setHeader("content-type", "text/html");
        response.end("<!doctype html><title>Other site</title><h1>Other site</h1>");
      });
      await new Promise<void>((resolve) => hostile.listen(0, "127.0.0.1", resolve));
      browser = await chromium.launch();
    });

    test.afterAll(async () => {
      await browser.close();
      await new Promise((resolve) => hostile.close(resolve));
    });

    // The refused request logs on the other site's page, not the app, so that page runs in a browser the guard does not watch.
    test("REQ-BT-047 TM-1936 a page from another origin posts to the API and is refused, nothing is created", async () => {
      const context = await browser.newContext({ baseURL: BASE });
      const app: Page = await context.newPage();
      await signIn(app, "olivia");
      expect((await sessionCookies(context)).length, "the victim holds a session cookie").toBeGreaterThan(0);

      const other = await context.newPage();
      await other.goto(`http://127.0.0.1:${(hostile.address() as AddressInfo).port}/`);
      const product = productName("forged");
      await other.evaluate(
        async ({ url, body }) => {
          await fetch(url, { method: "POST", mode: "no-cors", credentials: "include", headers: { "content-type": "text/plain" }, body }).catch(() => undefined);
        },
        { url: `${API}/batches`, body: JSON.stringify({ farmId: "farm-a", product }) },
      );
      const rows = await api.list("ada", 50);
      expect(rows.filter((row) => row.product === product)).toEqual([]);
      await context.close();
    });
  });

  test("REQ-BT-047 TM-1937 a batch page holds no token in its links", async ({ actor }) => {
    const batch = await api.batchAt("Created", "farm-b", "link");
    const page = await actor("olivia");
    await page.goto(batchPath(batch));
    await expect(page.getByRole("heading", { level: 1, name: batch.product })).toBeVisible();
    expect(await page.locator("a[href]").evaluateAll((links) => links.map((link) => link.getAttribute("href")).filter((href) => /token=|eyJ/.test(href ?? "")))).toEqual([]);
  });
});
