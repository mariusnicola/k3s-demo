import { parseAst } from "vite";
/** Each rule takes a path relative to src plus the text, and returns findings. Pure, so tests can feed planted violations. */

const NAMED_COLOURS =
  "red|green|blue|white|black|gray|grey|orange|yellow|purple|pink|brown|cyan|magenta|teal|navy|silver|maroon|olive|lime|aqua|fuchsia|gold|coral|salmon|crimson|indigo|violet";
const COLOUR_PROPS = "color|background|background-color|fill|stroke|border|border-color|border-top|border-bottom|border-left|border-right|outline|outline-color|box-shadow|accent-color|caret-color";

const RAW_COLOUR = [
  /#[0-9a-fA-F]{3,8}\b/,
  /\b(?:rgb|rgba|hsl|hsla|hwb|lab|lch|oklab|oklch|color-mix)\(/,
  new RegExp(`\\b(?:${COLOUR_PROPS})\\s*:[^;}\\n]*\\b(?:${NAMED_COLOURS})\\b`, "i"),
  new RegExp(`\\b(?:fill|stroke|color|background|backgroundColor)\\s*[=:]\\s*\\{?["'\`](?:${NAMED_COLOURS})["'\`]`, "i"),
];

function lines(text, test) {
  const found = [];
  text.split(/\r?\n/).forEach((line, index) => {
    if (test(line)) found.push(index + 1);
  });
  return found;
}

function finding(path, rule, numbers) {
  return numbers.map((line) => `${path}:${line} ${rule}`);
}

export function isTest(path) {
  return /\.test\.tsx?$/.test(path) || path.startsWith("test/") || path.startsWith("mocks/");
}

export function checkRawColour(path, text) {
  if (path === "ds/tokens.css") return [];
  return finding(path, "raw colour outside tokens.css", lines(text, (l) => RAW_COLOUR.some((p) => p.test(l))));
}

export function checkPageIsolation(path, text) {
  if (!path.startsWith("pages/") && path !== "App.tsx") return [];
  return [
    ...finding(path, "pages use ds components, no native control or table tags", lines(text, (l) => /<(button|input|select|textarea|table)\b/.test(l))),
    ...finding(path, "pages carry no className or style", lines(text, (l) => /\b(className|style)=/.test(l))),
    ...finding(path, "pages import no css", lines(text, (l) => /import\s+["'].*\.css["']/.test(l))),
  ];
}

export function checkRadixInDs(path, text) {
  if (path.startsWith("ds/")) return [];
  return finding(path, "radix only inside ds", lines(text, (l) => /from\s+["']@radix-ui\//.test(l)));
}

export function checkMocks(path, text) {
  if (isTest(path)) return [];
  return finding(path, "mocks only in tests", lines(text, (l) => /from\s+["'][^"']*mocks\//.test(l)));
}

export function checkNoPolling(path, text) {
  const found = [];
  if (path !== "live/clock.ts") found.push(...finding(path, "setInterval only in live/clock.ts, no polling", lines(text, (l) => /\bsetInterval\(/.test(l))));
  if (path !== "api/client.ts") found.push(...finding(path, "fetch only in api/client.ts", lines(text, (l) => /\bfetch\(/.test(l))));
  return found;
}

const READ = /\bclient\.|\bfetch\(|\.(?:reload|refresh)\(/;

/** The text of each socket or hop handler, found by matching its brackets, so a read inside one cannot hide on another line. */
function handlers(text) {
  const found = [];
  for (const match of text.matchAll(/\b(?:useHopFeed|useLineFeed|onHops|onFrame|onData|onmessage)\b\s*(?:\(|=)/g)) {
    const start = match.index + match[0].length;
    const called = match[0].endsWith("(");
    // A JSX prop ends at its brace, an assignment at its semicolon, a call at its parenthesis.
    const prop = !called && text[start] === "{";
    let depth = called ? 1 : 0;
    let end = start;
    for (; end < text.length; end += 1) {
      const char = text[end];
      if ("([{".includes(char)) depth += 1;
      else if (")]}".includes(char)) depth -= 1;
      if (depth === 0 && (called || prop ? ")]}".includes(char) : char === ";")) break;
    }
    found.push({ line: text.slice(0, match.index).split("\n").length, body: text.slice(start, end) });
  }
  return found;
}

export function checkNoReadInHandler(path, text) {
  return finding(path, "no read inside a socket or hop handler, apply the frame", handlers(text).filter((h) => READ.test(h.body)).map((h) => h.line));
}

export function checkBaseUrls(path, text) {
  if (path === "config.ts" || path === "vite.config.ts") return [];
  return finding(path, "base URLs only in config.ts", lines(text, (l) => /\b(?:https?|wss?):\/\//.test(l) || /192\.168\./.test(l)));
}

export function checkCopy(path, text) {
  const emoji = /[\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}]/u;
  return finding(path, "no em dash, en dash or emoji", lines(text, (l) => /[–—]/.test(l) || emoji.test(l)));
}

const METHOD_LIMIT = 30;
const FOLDER_LIMIT = 15;
const FUNCTIONS = new Set(["FunctionDeclaration", "FunctionExpression", "ArrowFunctionExpression"]);

const isTestFile = (path) => /\.test\.tsx?$/.test(path);

function fileLimit(path) {
  if (isTestFile(path)) return 300;
  if (path.endsWith(".tsx")) return 250;
  return path.endsWith(".css") ? 300 : 200;
}

const lineOf = (text, offset) => text.slice(0, offset).split("\n").length;

/** A describe groups tests and holds no logic of its own, so its callback is not a method. */
function isDescribe(call) {
  const callee = call?.callee;
  const named = callee?.type === "CallExpression" ? callee.callee : callee;
  return (named?.object ?? named)?.name === "describe";
}

function visit(node, parent, found, grouped) {
  if (FUNCTIONS.has(node.type) && !(grouped && parent?.type === "CallExpression" && isDescribe(parent))) found.push(node);
  for (const child of Object.values(node)) {
    for (const item of Array.isArray(child) ? child : [child]) {
      if (typeof item?.type === "string") visit(item, node, found, grouped);
    }
  }
}

function longMethods(path, text) {
  const found = [];
  visit(parseAst(text, { lang: path.endsWith("x") ? "tsx" : "ts" }), null, found, isTestFile(path));
  return found.flatMap((node) => {
    const first = lineOf(text, node.start);
    const count = lineOf(text, node.end) - first + 1;
    return count > METHOD_LIMIT ? [`${path}:${first}: method of ${count} lines, limit ${METHOD_LIMIT}`] : [];
  });
}

/** REQ-BT-057. Separate from lintSource because tests are held to these limits too. */
export function checkSize(path, text) {
  const count = text.trimEnd().split("\n").length;
  const limit = fileLimit(path);
  const file = count > limit ? [`${path}: file of ${count} lines, limit ${limit}`] : [];
  return path.endsWith(".css") ? file : [...file, ...longMethods(path, text)];
}

export function checkFolders(paths) {
  const counts = new Map();
  for (const path of paths) {
    const folder = path.split("/").slice(0, -1).join("/");
    counts.set(folder, (counts.get(folder) ?? 0) + 1);
  }
  return [...counts].filter(([, count]) => count > FOLDER_LIMIT).map(([folder, count]) => `${folder}: folder of ${count} files, limit ${FOLDER_LIMIT}`);
}

export const SOURCE_RULES = [checkRawColour, checkPageIsolation, checkRadixInDs, checkMocks, checkNoPolling, checkNoReadInHandler, checkBaseUrls, checkCopy];

export function lintSource(path, text) {
  if (isTest(path)) return [];
  return SOURCE_RULES.flatMap((rule) => rule(path, text));
}
