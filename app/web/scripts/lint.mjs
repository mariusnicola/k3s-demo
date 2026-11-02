import { readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { checkCopy, checkFolders, checkSize, lintSource } from "./lint-rules.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const src = join(root, "src");
const docs = [resolve(root, "../../docs/architecture/web.md"), resolve(root, "../../docs/architecture/design-system.md")];

function walk(dir) {
  return readdirSync(dir).flatMap((name) => {
    const full = join(dir, name);
    return statSync(full).isDirectory() ? walk(full) : [full];
  });
}

const sources = walk(src).filter((f) => /\.(ts|tsx|css)$/.test(f));
const paths = sources.map((file) => relative(src, file).split(sep).join("/"));
const findings = sources.flatMap((file, i) => {
  const text = readFileSync(file, "utf8");
  return [...lintSource(paths[i], text), ...checkSize(paths[i], text)];
});
findings.push(...checkFolders(paths));
findings.push(...lintSource("index.html", readFileSync(join(root, "index.html"), "utf8")));
for (const doc of docs) findings.push(...checkCopy(relative(root, doc).split(sep).join("/"), readFileSync(doc, "utf8")));

if (findings.length > 0) {
  for (const f of findings) console.error(`[ERROR] ${f}`);
  console.error(`[ERROR] ${findings.length} lint finding(s)`);
  process.exit(1);
}
console.log(`[OK] lint clean, ${sources.length + 1} source files and ${docs.length} docs checked`);
