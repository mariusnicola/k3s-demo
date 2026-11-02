# Code rules

The rules below apply to every file under `app/`, `deploy/` and `tests/` (REQ-BT-057). Enforcement covers less: see Enforcement.

## Principles
- SRP: one reason to change per file, class, method and folder.
- DRY: one fact, one place.
- KISS and YAGNI: build only what a requirement names.
- Clean architecture: dependencies point inward, checked by the architecture tests.
- Prefer one entry and one exit in each method. Guard clauses at the top are the only early returns.

## Method limit
- At most 30 lines, 20 preferred. Counted from signature to closing line.
- A React component is a method.
- A nested function counts on its own and also inside the method holding it.

## File limits, in lines

| Type | Limit |
|---|---|
| C# source | 200 |
| TypeScript source | 200 |
| TSX source | 250 |
| CSS | 300 |
| PowerShell | 300 |
| YAML, Helm template | 150 |
| Test file, any language | 300 |

## Folders
- One folder, one topic. Folder names say what the code does.
- A folder past 15 files splits by topic.

## Exemptions
- Lock files and generated output are outside the limits.
- The callback of a `describe` in a test file is not a method. The `it` bodies inside it are. A test file is a name ending in `.test.ts` or `.test.tsx`.
- CSS has a file limit and no method limit.
- Nothing else is exempt. Support code in `src/mocks/` and `src/test/` keeps the source limits unless it is a test file.

## Enforcement
- Enforced today, web: `app/web/src`, TypeScript, TSX and CSS. `checkSize` and `checkFolders` in `app/web/scripts/lint-rules.mjs` check the file, method and folder limits. The 20 line preference is not checked.
- The web check runs in `npm run lint`, in `npm test` through the REQ-BT-033 test, and in both of those as gate steps of `deploy/release.ps1`.
- `npm run build` does not run the web check. No CI exists.
- Enforced today, C#: every `.cs` file under `app`, `deploy` and `tests`, build output excluded. `SizeLimitTests` in `app/tests/BatchTracker.ArchTests` checks the file, method and folder limits. The 20 line preference is not checked.
- The C# check runs in the architecture tests step of `deploy/release.ps1`.
- Not yet enforced, tracked by T-008: PowerShell, YAML and Helm templates, `tests/e2e`, and `app/web/scripts`.
