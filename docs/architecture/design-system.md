# Design system


## Design system
Three token layers: primitive, semantic, component. CSS variables, light and dark. Components in `app/web/src/ds`, used by every page, catalog at `/design-system`. Lint fails on raw colours outside tokens. Token test checks WCAG AA contrast for every text and background pair.

## Direction
Cluster Terrain: the running system is a place you look at. Deep petrol ground, one coral accent, isometric plates. Professional first, every label plain.
- Type: Hanken Grotesk, JetBrains Mono for ids, hashes and times. Tabular figures everywhere. Both self-hosted.
- Colour: deep petrol ground and left rail, coral for the accent and the moving route, teal for services. Failure is red, never the accent. The rail stays deep petrol in the light theme.
- Status is never colour alone: each tone has an icon or word, a failed dot is a diamond, a disabled button has a dashed edge, a pod not serving stands lower and carries a mark.
- Signature: the cluster map. One plate per node plus Docker, one block per service, one small block per pod on its real node.
- Motion: only where it carries meaning, hop dots, the route and loading. Off under reduced motion, and the route stops on lost data.

## Cluster map
- `ArchitectureDiagram` takes `MonitorReport.pods`. Geometry lives in `src/logic/terrain/`, with no DOM, so each rule has a test.
- A pod in the `batch-tracker` namespace is one object on the plate named by its node. A scale up adds one and a scale down removes one. A pod with no node stands on a Not placed plate.
- Service comes from the pod name: `api`, `dispatcher`, `worker`, each with any suffix. Everything else is an other pod.
- Pod heights: ready and not reported 0.9, not ready and unknown 0.55, terminating 0.35, pending and failed 0.15. Not ready, unknown, pending, failed and terminating each carry a mark.
- A dot goes to the pod its hop names, and to the service block when the hop names none or that pod is gone.
- Dots use a fixed pool moved by `getFrame`: no React render per frame.
- Free slots are not drawn: the report has no maximum. Terminating pods need a monitor field: the report leaves them out.
- At 720 px and below the plates stack straight down the screen, blocks stand low enough that none stands over another, and pod labels give way to the mark. The picture's text alternative names each node, its pods and each pod not serving.

## Dashboards by role
- `/dashboard` is every role's landing page and picks by permission: `ops.admin` the cluster view, `batch.inspect` inspection, `batch.write` operator, otherwise read only. Operator and inspector open on their queue, stats beside it.
- `FlowMap` draws batches per state as blocks on one plate, with Rejected branching off inspection. Every block carries its number.
- `DataTable tiles` lays rows out as tiles and stays a table to a screen reader. `KeyValues figures` is a grid of large numbers over small labels.
- A count built from a list at its limit reads "At least N". A page never tells a signed-in user to sign in as someone else.

## Operations
- A cluster operation with steps shows every step from its start, each with a mark and a word. One without steps stays one line.
- The running clock and the estimate are `aria-live="off"`; only a finished step is announced, once.
- Operations are a toast, fixed top right, 24rem wide at most, never catching a press outside itself and never moving the page. Past 70% of the screen height it scrolls inside; long step lists scroll in their own box; failure details stay folded.

## Errors
- Every error reads in one order through `ErrorDetail`: what happened, where, what it did to the batch, what to do next, correlation ID, report ID. A row with nothing to say is left out.
- Exception type, message and stack sit in a closed Technical detail section.
- A failure shows the server's ID when it gave one, else the ID the browser sent, also when no server answered.
- A failure the browser reported also shows a report ID, never an ordinary 4xx, in `ErrorDetail`, `CrashState` and `ConnectionStatus`, through `ReportId`: the ID, a copy control, and for users with `ops.admin` a link to Logs. It shows even when the report was lost.
- The correlation ID has a copy control, and a link to Logs filtered on it for users with `ops.admin`. Others would be refused there, so they get the ID alone.

## Rules
- `src/ds/tokens.css` is the only file with raw colours. `src/ds/contrast-pairs.ts` lists every pair the token test checks, 4.5:1 for text, 3:1 for control edges and data marks.
- Pages import UI from `src/ds` only: no native controls, no className, no style. `npm run lint` enforces this, plus no polling, no base URLs outside config, no mocks in app code, no em dashes or emoji.
- Every component exported from `src/ds/index.ts` needs a catalog entry in `src/pages/design-system/catalog.tsx`. `src/ds/catalog.test.tsx` fails otherwise, and lists the states each component must show.
- Control height 40 px, 44 px at 720 px wide or below and on touch. Left rail above 960 px, top bar at or below it. Tables stack into label and value rows at 720 px.
