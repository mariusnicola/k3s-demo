import { readFileSync, readdirSync } from "node:fs";
import { resolve } from "node:path";
import { contrastPairs } from "./contrast-pairs";

const css = ["tokens.css", "component-tokens.css"].map((file) => readFileSync(resolve(__dirname, file), "utf8")).join("\n").replace(/\/\*[\s\S]*?\*\//g, "");

function declarations(body: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const d of body.matchAll(/(--[\w-]+):\s*([^;]+);/g)) out[d[1]!] = d[2]!.trim();
  return out;
}

function themeBlocks(): { root: Record<string, string>; light: Record<string, string>; dark: Record<string, string> } {
  const out = { root: {}, light: {}, dark: {} };
  for (const m of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const selector = m[1]!.trim();
    const decls = declarations(m[2]!);
    if (selector === ":root") Object.assign(out.root, decls);
    else if (selector.includes('[data-theme="light"]')) Object.assign(out.light, decls);
    else if (selector === '[data-theme="dark"]') Object.assign(out.dark, decls);
  }
  return out;
}

const blocks = themeBlocks();
const themes = {
  light: { ...blocks.root, ...blocks.light },
  dark: { ...blocks.root, ...blocks.light, ...blocks.dark },
};

function resolveVar(theme: Record<string, string>, name: string, depth = 0): string {
  if (depth > 10) throw new Error(`cycle at ${name}`);
  const value = theme[name];
  if (!value) throw new Error(`token ${name} not defined`);
  const ref = /^var\((--[\w-]+)\)$/.exec(value);
  return ref ? resolveVar(theme, ref[1]!, depth + 1) : value;
}

function luminance(hex: string): number {
  const v = /^#([0-9a-f]{6})$/i.exec(hex);
  if (!v) throw new Error(`not a 6 digit hex: ${hex}`);
  const channels = [0, 2, 4].map((i) => parseInt(v[1]!.slice(i, i + 2), 16) / 255);
  const [r, g, b] = channels.map((c) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4)) as [number, number, number];
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (hi + 0.05) / (lo + 0.05);
}

describe.each(Object.entries(themes))("tokens %s theme", (_name, theme) => {
  it.each(contrastPairs.map((p) => [p.fg, p.bg, p.min] as const))("REQ-BT-033 %s on %s meets %d:1", (fg, bg, min) => {
    expect(contrast(resolveVar(theme, fg), resolveVar(theme, bg))).toBeGreaterThanOrEqual(min);
  });
});

describe("Cluster Terrain direction", () => {
  it("REQ-BT-033 the type is Hanken Grotesk and JetBrains Mono, with a system fallback", () => {
    expect(themes.dark["--font-sans"]).toMatch(/^"Hanken Grotesk Variable",.*sans-serif$/);
    expect(themes.dark["--font-mono"]).toMatch(/^"JetBrains Mono Variable",.*monospace$/);
  });

  it("REQ-BT-033 dark is the deep petrol ground with the coral accent", () => {
    expect(resolveVar(themes.dark, "--bg-canvas")).toBe("#0b2f36");
    expect(resolveVar(themes.dark, "--accent")).toBe("#ffb398");
  });

  it("REQ-BT-033 the rail stays deep petrol in both themes", () => {
    expect(resolveVar(themes.light, "--bg-chrome")).toBe(resolveVar(themes.dark, "--bg-chrome"));
  });

  it("REQ-BT-033 failure is a different colour from the accent, in both themes", () => {
    for (const theme of Object.values(themes)) expect(resolveVar(theme, "--danger")).not.toBe(resolveVar(theme, "--accent"));
  });
});

describe("rail layout", () => {
  const sheets = readdirSync(__dirname, { recursive: true, encoding: "utf8" }).filter((file) => file.endsWith(".css") && !file.endsWith("tokens.css"));
  const components = sheets.map((file) => readFileSync(resolve(__dirname, file), "utf8")).join("\n");

  it("REQ-BT-038 the shell is a left rail beside the page on wide screens, and stacks on small ones", () => {
    expect(components).toMatch(/\.ds-shell \{[^}]*grid-template-columns: var\(--rail-width\) minmax\(0, 1fr\)/);
    expect(components).toMatch(/@media \(max-width: 960px\) \{\s*\.ds-shell \{[^}]*grid-template-columns: minmax\(0, 1fr\)/);
  });

  it("REQ-BT-028 no link animates by itself, on an idle system or any other", () => {
    expect(components).not.toMatch(/ds-track/);
    expect(components).toMatch(/\.ds-diagram\[data-lost\] \.ds-map-link \{/);
  });
});

describe("token coverage", () => {
  it("REQ-BT-033 every text token has a contrast pair", () => {
    const paired = new Set(contrastPairs.map((p) => p.fg));
    const textTokens = Object.keys(themes.dark).filter((k) => /^--(text-|tone-\w+-fg)/.test(k));
    expect(textTokens.length).toBeGreaterThan(8);
    expect(textTokens.filter((k) => !paired.has(k))).toEqual([]);
  });

  it("REQ-BT-033 every semantic token is set in the dark theme too", () => {
    expect(Object.keys(blocks.light).filter((k) => !(k in blocks.dark))).toEqual([]);
  });

  it("REQ-BT-033 the contrast calculation rejects a failing pair", () => {
    expect(contrast("#777777", "#ffffff")).toBeLessThan(4.5);
    expect(contrast("#000000", "#ffffff")).toBeCloseTo(21, 5);
  });
});
