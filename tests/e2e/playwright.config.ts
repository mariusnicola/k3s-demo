import { defineConfig } from "@playwright/test";
import { BASE } from "./support/env";

export default defineConfig({
  testDir: "./specs",
  globalSetup: "./support/global-setup.ts",
  // One worker: the line is one shared machine, and the idle network rows need a silent system.
  workers: 1,
  fullyParallel: false,
  retries: 0,
  forbidOnly: true,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  outputDir: "./test-results",
  reporter: [["list"], ["json", { outputFile: "./test-results/results.json" }]],
  use: {
    baseURL: BASE,
    viewport: { width: 1440, height: 900 },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [{ name: "chromium", use: { browserName: "chromium" } }],
});
