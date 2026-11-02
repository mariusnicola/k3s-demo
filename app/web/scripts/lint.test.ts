import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { checkFolders, checkSize, lintSource } from "./lint-rules.mjs";

const lint = lintSource as (path: string, text: string) => string[];
const size = checkSize as (path: string, text: string) => string[];
const folders = checkFolders as (paths: string[]) => string[];
const filled = (lines: number) => "// line\n".repeat(lines);
const fn = (lines: number) => `export function big() {\n${"  work();\n".repeat(lines - 2)}}\n`;

describe("REQ-BT-057 size limits", () => {
  it.each([
    ["pages/A.tsx", 250],
    ["logic/a.ts", 200],
    ["ds/a.css", 300],
    ["pages/a.test.tsx", 300],
    ["logic/a.test.ts", 300],
  ])("REQ-BT-057 %s stops at %i lines", (path, limit) => {
    expect(size(path, filled(limit))).toEqual([]);
    expect(size(path, filled(limit + 1)).join()).toContain(`${limit + 1} lines`);
  });

  it("REQ-BT-057 a method stops at 30 lines, signature to closing line", () => {
    expect(size("logic/a.ts", fn(30))).toEqual([]);
    expect(size("logic/a.ts", fn(31)).join()).toContain("31 lines");
  });

  it("REQ-BT-057 a component, an arrow, a class method and an inner function all count", () => {
    const body = "  work();\n".repeat(30);
    expect(size("pages/A.tsx", `export function A() {\n${body}  return <div />;\n}\n`)).toHaveLength(1);
    expect(size("logic/a.ts", `const a = () => {\n${body}};\n`)).toHaveLength(1);
    expect(size("logic/a.ts", `class A {\n  run() {\n${body}  }\n}\n`)).toHaveLength(1);
    expect(size("logic/a.ts", `function a() {\n  return () => {\n${body}  };\n}\n`).length).toBeGreaterThan(0);
  });

  it("REQ-BT-057 the finding names the line it starts on", () => {
    expect(size("logic/a.ts", `// first\n${fn(31)}`).join()).toContain("logic/a.ts:2");
  });

  it("REQ-BT-057 a test body counts, a describe does not", () => {
    const body = "  check();\n".repeat(40);
    expect(size("pages/a.test.tsx", `describe("group", () => {\n${"  it('x', () => check());\n".repeat(40)}});\n`)).toEqual([]);
    expect(size("pages/a.test.tsx", `it("long", () => {\n${body}});\n`)).toHaveLength(1);
  });

  it("REQ-BT-057 production code cannot escape by naming a function describe", () => {
    const body = "  work();\n".repeat(40);
    expect(size("logic/a.ts", `describe("x", () => {\n${body}});\n`)).toHaveLength(1);
    expect(size("mocks/fixtures.ts", `describe("x", () => {\n${body}});\n`)).toHaveLength(1);
  });

  it.each([
    ["mocks/fixtures.ts", 200],
    ["mocks/harness.tsx", 250],
    ["test/setup.ts", 200],
  ])("REQ-BT-057 support file %s keeps the source limit of %i", (path, limit) => {
    expect(size(path, filled(limit))).toEqual([]);
    expect(size(path, filled(limit + 1)).join()).toContain(`${limit + 1} lines`);
  });

  it("REQ-BT-057 a test file under mocks is still a test file", () => {
    expect(size("mocks/harness.test.tsx", filled(300))).toEqual([]);
  });

  it("REQ-BT-057 the parser comes from vite, a direct dependency", () => {
    expect(readFileSync(resolve(import.meta.dirname, "lint-rules.mjs"), "utf8")).not.toContain("rolldown");
  });

  it("REQ-BT-057 a folder holds 15 files, counting tests, and sub folders count apart", () => {
    const names = (dir: string, n: number) => Array.from({ length: n }, (_, i) => `${dir}/f${i}.ts`);
    expect(folders(names("pages", 15))).toEqual([]);
    expect(folders(names("pages", 16)).join()).toContain("pages");
    expect(folders([...names("pages", 10), ...names("pages/logs", 10)])).toEqual([]);
  });
});

describe("lint rules", () => {
  it.each([
    ["ds/components.css", ".a { color: #ff0000; }"],
    ["ds/components.css", ".a { background: rgb(1, 2, 3); }"],
    ["ds/components.css", ".a { background: hsl(1 2% 3%); }"],
    ["ds/components.css", ".a { border: 1px solid red; }"],
    ["ds/components.css", ".a { box-shadow: 0 0 2px oklch(1 0 0); }"],
    ["ds/Button.tsx", 'const x = <svg fill="#abc" />;'],
    ["ds/Button.tsx", '<path fill="red" />'],
    ["pages/X.tsx", "const c = { color: 'white' };"],
  ])("REQ-BT-033 raw colour fails in %s: %s", (path, text) => {
    expect(lint(path, text).join()).toContain("raw colour");
  });

  it("REQ-BT-033 tokens.css may hold raw colours, others may use them through var", () => {
    expect(lint("ds/tokens.css", "--x: #ffffff;")).toEqual([]);
    expect(lint("ds/components.css", ".a { color: var(--text-primary); background: transparent; }")).toEqual([]);
  });

  it("REQ-BT-033 an anchor link is not a colour", () => {
    expect(lint("ds/Layout.tsx", '<a href="#main">')).toEqual([]);
  });

  it.each([
    ["pages/X.tsx", '<button type="button">x</button>', "native control"],
    ["pages/X.tsx", "<table>", "native control"],
    ["pages/X.tsx", '<div className="x" />', "className"],
    ["pages/X.tsx", "<div style={{}} />", "className or style"],
    ["pages/X.tsx", 'import "./x.css";', "import no css"],
    ["pages/X.tsx", 'import * as D from "@radix-ui/react-dialog";', "radix"],
    ["pages/X.tsx", 'import { a } from "../mocks/fixtures";', "mocks"],
    ["pages/X.tsx", "setInterval(load, 1000);", "polling"],
    ["pages/X.tsx", 'fetch("/api");', "fetch only"],
    ["pages/X.tsx", 'const u = "ws://<HOST_IP>:5320/ws";', "base URLs"],
    ["pages/X.tsx", "const copy = 'one — two';", "em dash"],
    ["ds/Button.tsx", "const copy = '\u{1F680}';", "emoji"],
  ])("REQ-BT-034 %s fails on %s", (path, text, expected) => {
    expect(lint(path, text).join()).toContain(expected);
  });

  it.each([
    ["useHopFeed((hops) => { void client.alerts(); });"],
    ["const onHops = useCallback((hops: Hop[]) => { list.reload(); }, []);"],
    ["useLineFeed((frame) => alerts.refresh());"],
    ["const onFrame = (frame: LineFrame) => {\n  setStatus(frame.status);\n  void client.lineLogs(filters);\n};"],
    ["socket.onmessage = () => { void client.me(); };"],
  ])("REQ-BT-032 a read inside a socket or hop handler fails: %s", (text) => {
    expect(lint("pages/X.tsx", text).join()).toContain("no read inside a socket");
  });

  it("REQ-BT-032 a handler that only applies its frame passes, and a read elsewhere passes", () => {
    expect(lint("pages/X.tsx", "useHopFeed((hops) => flow.ingest(hops));\nconst load = () => client.alerts();")).toEqual([]);
  });

  it("REQ-BT-032 only the clock file may use setInterval, and it never fetches", () => {
    expect(lint("live/clock.ts", "setInterval(tick, 1000);")).toEqual([]);
    expect(lint("live/clock.ts", 'fetch("/api");').join()).toContain("fetch only");
  });

  it("REQ-BT-033 the lint script passes on this repository as it stands", () => {
    const out = execFileSync("node", ["scripts/lint.mjs"], { cwd: resolve(__dirname, ".."), encoding: "utf8" });
    expect(out).toContain("[OK]");
  });
});
