import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "../support/fixtures";

const here = dirname(fileURLToPath(import.meta.url));

function specs(): { name: string; text: string }[] {
  return readdirSync(here)
    .filter((name) => name.endsWith(".spec.ts") && name !== "suite-rules.spec.ts")
    .map((name) => ({ name, text: readFileSync(join(here, name), "utf8") }));
}

// Built from parts so this file does not trip its own scan of the other specs.
const BANNED: [string, RegExp][] = [
  ["a test outside the guarded fixture", new RegExp(`from "@playwright/${"test"}"`)],
  ["a mocked response", new RegExp(`route\\.${"fulfill"}|page\\.${"route"}\\(|routeWeb${"Socket"}`)],
  ["a skipped test", new RegExp(`test\\.(${"skip"}|${"fixme"}|${"only"})`)],
  ["a soft assertion", new RegExp(`expect\\.${"soft"}`)],
  ["a host that bypasses the app origin", new RegExp(`https?://(?!127\\.)\\d{1,3}(\\.\\d{1,3}){3}`)],
];

test.describe("suite rules", () => {
  test("REQ-BT-040 TM-948 every spec imports test from the guarded fixture", () => {
    const outside = specs().filter(({ text }) => !/import \{[^}]*\btest\b[^}]*\} from "\.\.\/support\/fixtures"/.test(text));
    expect(outside.map(({ name }) => name)).toEqual([]);
  });

  for (const [what, pattern] of BANNED) {
    test(`REQ-BT-040 TM-953 no spec holds ${what}`, () => {
      const hits = specs().flatMap(({ name, text }) =>
        text
          .split("\n")
          .filter((line) => pattern.test(line) && !line.startsWith("import type"))
          .map((line) => `${name}: ${line.trim()}`),
      );
      expect(hits).toEqual([]);
    });
  }
});
