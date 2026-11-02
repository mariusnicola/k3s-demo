import { chromium, expect, test as base, type Browser, type BrowserContext, type BrowserContextOptions, type Page, type TestInfo } from "@playwright/test";
import { api, line as lineApi, type Batch, type Farm, type State } from "./api";
import { BASE, type User } from "./env";
import { ConsoleGuard } from "./guard";
import { Relay } from "./relay";
import { signIn } from "./ui";

export { chromium, expect };

type Actor = (user: User, options?: BrowserContextOptions) => Promise<Page>;

export interface RelayedBrowser {
  relay: Relay;
  /** A signed-in page whose traffic to the app runs through the relay. */
  actor: Actor;
}

export interface LineControl {
  start: (rate: number) => Promise<void>;
  stop: () => Promise<void>;
}

interface Fixtures {
  guard: ConsoleGuard;
  actor: Actor;
  relayed: RelayedBrowser;
  /** Makes a blocked batch, and replays it when the test ends so Alerts stay clean. */
  blocked: (state?: State, farm?: Farm) => Promise<Batch>;
  /** Restores the line to what the test found, and waits for its walks to end. */
  line: LineControl;
}

function failOnProblems(guard: ConsoleGuard, info: TestInfo): void {
  const problems = guard.verdict();
  if (problems.length === 0) return;
  throw new Error(`Front end logged ${problems.length} problem(s) in "${info.title}":\n${problems.join("\n")}`);
}

function contextsAlwaysWatched(browser: Browser, guard: ConsoleGuard): () => void {
  const original = browser.newContext.bind(browser);
  browser.newContext = async (options?: BrowserContextOptions) => {
    const context = await original(options);
    guard.watch(context);
    return context;
  };
  return () => {
    browser.newContext = original;
  };
}

const NEXT_STATE: Partial<Record<State, State>> = { Created: "Dose1", Dose1: "Dose2", Inspected: "Shipped", Shipped: "Closed" };

export const test = base.extend<Fixtures>({
  guard: [
    async ({ browser }, use, info) => {
      const guard = new ConsoleGuard();
      const restore = contextsAlwaysWatched(browser, guard);
      await use(guard);
      restore();
      failOnProblems(guard, info);
    },
    { auto: true },
  ],

  context: async ({ context, guard }, use) => {
    guard.watch(context);
    await use(context);
  },

  actor: async ({ browser }, use) => {
    const contexts: BrowserContext[] = [];
    await use(async (user, options) => {
      const context = await browser.newContext({ baseURL: BASE, viewport: { width: 1440, height: 900 }, ...options });
      contexts.push(context);
      const page = await context.newPage();
      await signIn(page, user);
      return page;
    });
    for (const context of contexts) await context.close();
  },

  relayed: async ({ guard }, use) => {
    const relay = new Relay();
    await relay.start();
    const browser = await chromium.launch({ args: [relay.hostRule] });
    await use({
      relay,
      actor: async (user, options) => {
        const context = await browser.newContext({ baseURL: BASE, viewport: { width: 1440, height: 900 }, ...options });
        guard.watch(context);
        const page = await context.newPage();
        await signIn(page, user);
        return page;
      },
    });
    await browser.close();
    await relay.stop();
  },

  blocked: async ({}, use) => {
    const made: Batch[] = [];
    await use(async (state = "Created", farm = "farm-b") => {
      const batch = await api.batchAt(state, farm, "blocked");
      made.push(batch);
      await api.failNext(batch);
      await api.advance("olivia", batch, NEXT_STATE[state]!);
      await api.dead(batch);
      return batch;
    });
    for (const batch of made) {
      if ((await api.read("ada", batch)).batch.pending?.status === "dead") await api.replay(batch);
    }
  },

  line: async ({}, use) => {
    const found = await lineApi.status();
    await use({
      start: async (rate) => {
        await lineApi.start(rate);
      },
      stop: async () => {
        await lineApi.stop();
      },
    });
    if (found.running) await lineApi.start(found.rate);
    else await lineApi.stop();
    if (!found.running) await lineApi.quiet();
    expect((await lineApi.status()).running, "line left as found").toBe(found.running);
  },
});
