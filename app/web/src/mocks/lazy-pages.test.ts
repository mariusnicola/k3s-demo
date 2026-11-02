import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const read = (path: string) => readFileSync(resolve(__dirname, path), "utf8");

describe("B-096 a lazy page never races a find", () => {
  it("B-096 every lazy page is imported with the harness, so its first load falls outside a test's wait", () => {
    const pages = [...read("../App.tsx").matchAll(/lazy\(\(\) => import\("\.\/([^"]+)"\)/g)].map((m) => m[1]!);
    expect(pages.length).toBeGreaterThan(0);
    const harness = read("harness.tsx");
    for (const page of pages) expect(harness, page).toContain(`import "../${page}";`);
  });
});
