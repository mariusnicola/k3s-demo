import { createServer } from "node:http";
import { connect, type AddressInfo } from "node:net";
import type { Browser, Page } from "@playwright/test";
import { BASE } from "../support/env";
import { chromium, expect, test } from "../support/fixtures";
import { ConsoleGuard } from "../support/guard";

/**
 * Each case breaks a page on purpose and reads the verdict of its own guard, in a browser the
 * suite guard does not watch, so the deliberate break cannot turn this test red by accident.
 */
async function broken(browser: Browser, act: (page: Page, guard: ConsoleGuard) => Promise<void>): Promise<string[]> {
  const guard = new ConsoleGuard();
  const context = await browser.newContext({ baseURL: BASE });
  guard.watch(context);
  const page = await context.newPage();
  await page.goto("/signin");
  await act(page, guard);
  await context.close();
  return guard.verdict();
}

/** Stands in for the site and answers one path itself, so the browser meets a real refusal and no route is mocked. */
async function refusing(path: string, status: number, headers: Record<string, string> = {}): Promise<{ browser: Browser; close: () => Promise<void> }> {
  const site = createServer((request, response) => {
    if (request.url === path) {
      response.writeHead(status, headers).end();
      return;
    }
    void fetch(`${BASE}${request.url}`).then(async (answer) => {
      const type = answer.headers.get("content-type");
      response.writeHead(answer.status, type ? { "content-type": type } : {}).end(Buffer.from(await answer.arrayBuffer()));
    });
  });
  const host = new URL(BASE);
  // The page opens its sockets at load, so they pass through untouched.
  site.on("upgrade", (request, client, head) => {
    const upstream = connect(Number(host.port || 80), host.hostname, () => {
      const lines = request.rawHeaders.flatMap((_, i, all) => (i % 2 === 0 ? [`${all[i]}: ${all[i + 1]}`] : []));
      upstream.write(`${request.method} ${request.url} HTTP/1.1\r\n${lines.join("\r\n")}\r\n\r\n`);
      upstream.write(head);
      upstream.pipe(client).pipe(upstream);
    });
    upstream.on("error", () => client.destroy());
    client.on("error", () => upstream.destroy());
  });
  await new Promise<void>((resolve) => site.listen(0, "127.0.0.1", resolve));
  const { port } = site.address() as AddressInfo;
  const browser = await chromium.launch({ args: [`--host-resolver-rules=MAP ${host.hostname}:${host.port || 80} 127.0.0.1:${port}`] });
  return {
    browser,
    close: async () => {
      await browser.close();
      site.closeAllConnections();
      await new Promise((resolve) => site.close(resolve));
    },
  };
}

async function reportAnswered(path: string, status: number, headers?: Record<string, string>): Promise<string[]> {
  const site = await refusing(path, status, headers);
  const problems = await broken(site.browser, async (page) => {
    const seen = page.waitForResponse(new RegExp(`${path}$`));
    await page.evaluate((url) => void fetch(url, { method: "POST" }), path);
    await seen;
  });
  await site.close();
  return problems;
}

test.describe("console rule", () => {
  let browser: Browser;

  test.beforeAll(async () => {
    browser = await chromium.launch();
  });

  test.afterAll(async () => {
    await browser.close();
  });

  test("REQ-BT-040 TM-948 a clean page gives an empty verdict, so a failure means something", async () => {
    expect(await broken(browser, async () => undefined)).toEqual([]);
  });

  test("REQ-BT-040 TM-949 injected console.error is caught and named", async () => {
    const problems = await broken(browser, (page) => page.evaluate(() => console.error("injected error 949")));
    expect(problems).toHaveLength(1);
    expect(problems[0]).toContain("console.error: injected error 949");
  });

  test("REQ-BT-040 TM-949 injected console.warn is caught and named", async () => {
    const problems = await broken(browser, (page) => page.evaluate(() => console.warn("injected warning 949")));
    expect(problems).toHaveLength(1);
    expect(problems[0]).toContain("injected warning 949");
  });

  test("REQ-BT-040 TM-950 injected uncaught exception is caught", async () => {
    const problems = await broken(browser, async (page) => {
      const seen = page.waitForEvent("pageerror");
      await page.evaluate(() => {
        setTimeout(() => {
          throw new Error("injected exception 950");
        });
      });
      await seen;
    });
    expect(problems.join("\n")).toContain("uncaught page error: Error: injected exception 950");
  });

  test("REQ-BT-040 TM-951 injected unhandled promise rejection is caught", async () => {
    const problems = await broken(browser, async (page) => {
      const seen = page.waitForEvent("pageerror");
      await page.evaluate(() => {
        void Promise.reject(new Error("injected rejection 951"));
      });
      await seen;
    });
    expect(problems.join("\n")).toContain("injected rejection 951");
  });

  test("REQ-BT-040 TM-952 a page asset that fails to load is caught", async () => {
    const problems = await broken(browser, async (page) => {
      const failed = page.waitForEvent("requestfailed");
      await page.evaluate(() => {
        const image = document.createElement("img");
        image.src = "http://127.0.0.1:9/missing.png";
        document.body.append(image);
      });
      await failed;
    });
    expect(problems.join("\n")).toContain("failed request GET http://127.0.0.1:9/missing.png");
  });

  test("REQ-BT-040 TM-952 an unexpected 4xx is caught with its console line", async () => {
    const problems = await broken(browser, async (page) => {
      const seen = page.waitForResponse(/\/api\/me$/);
      await page.evaluate(() => void fetch("/api/me"));
      await seen;
    });
    expect(problems.join("\n")).toContain("unexpected response 401 GET");
    expect(problems.join("\n")).toContain("the server responded with a status of 401");
  });

  test("REQ-BT-056 a refused browser report passes, because a lost report is lost", async () => {
    expect(await reportAnswered("/api/browser-reports", 429, { "Retry-After": "1" })).toEqual([]);
  });

  test("REQ-BT-056 a report that fails with a 500 is still caught", async () => {
    expect((await reportAnswered("/api/browser-reports", 500)).join("\n")).toContain("unexpected response 500 POST");
  });

  test("REQ-BT-056 the same refusal on another route is still caught", async () => {
    expect((await reportAnswered("/api/browser-reports/other", 429)).join("\n")).toContain("unexpected response 429 POST");
  });

  test("REQ-BT-040 TM-954 a console error after the last step is still caught", async () => {
    const problems = await broken(browser, async (page) => {
      await page.getByRole("heading", { level: 1 }).waitFor();
      await page.evaluate(() => {
        queueMicrotask(() => console.error("injected late error 954"));
      });
    });
    expect(problems.join("\n")).toContain("injected late error 954");
  });

  test("REQ-BT-040 a declared 401 consumes its one console line and no more", async () => {
    const problems = await broken(browser, async (page, guard) => {
      guard.expectResponse("GET", /\/api\/me$/, 401);
      const seen = page.waitForResponse(/\/api\/me$/);
      await page.evaluate(() => {
        void fetch("/api/me");
        console.error("injected beside the declared response");
      });
      await seen;
    });
    expect(problems).toHaveLength(1);
    expect(problems[0]).toContain("injected beside the declared response");
  });

  test("REQ-BT-040 a second 401 beyond the one declared still fails", async () => {
    const problems = await broken(browser, async (page, guard) => {
      guard.expectResponse("GET", /\/api\/me$/, 401);
      const first = page.waitForResponse(/\/api\/me$/);
      await page.evaluate(() => void fetch("/api/me"));
      await first;
      const second = page.waitForResponse(/\/api\/me$/);
      await page.evaluate(() => void fetch("/api/me"));
      await second;
    });
    expect(problems.join("\n")).toContain("unexpected response 401 GET");
  });

  test("REQ-BT-040 a declared response that never happens fails the test", async () => {
    const problems = await broken(browser, async (_page, guard) => {
      guard.expectResponse("GET", /\/api\/me$/, 403);
    });
    expect(problems.join("\n")).toContain("declared response never happened");
  });
});
