# Requirements

How IDs reach tests: `docs/architecture/testing.md`.

| ID | Requirement | Pyramid level |
|---|---|---|
| REQ-BT-001 | Batch moves Created, Dose1, Dose2, Inspected, Shipped, Closed | unit, E2E |
| REQ-BT-002 | No skipped, repeated or backward step; Closed and Rejected are final | unit |
| REQ-BT-003 | Failed inspection goes to Rejected, reason required | unit, E2E |
| REQ-BT-004 | Create returns 201 and ETag | integration |
| REQ-BT-005 | State change without If-Match returns 428 | integration |
| REQ-BT-006 | Stale If-Match returns 412, nothing written | integration |
| REQ-BT-007 | Unknown batch 404, bad input 400, illegal move 409 | integration |
| REQ-BT-008 | Advance and inspect refused 409 while an event is pending or batch blocked; advance cannot reach Inspected | unit, integration |
| REQ-BT-009 | Every step reaches the queue at least once and gets exactly one compliance record | integration, E2E |
| REQ-BT-010 | Redelivered event changes nothing | integration |
| REQ-BT-011 | Worker 422 lands in dead letters; batch shows Blocked; replay completes it, and is refused 409 otherwise | integration, E2E |
| REQ-BT-012 | Compliance records form a hash chain; tampering is detected | unit |
| REQ-BT-013 | Dose 2 overdue flags once, repeat run adds none | unit, integration |
| REQ-BT-014 | Every API endpoint has a policy; only dev-login, healthz, ready, ready/deep are anonymous; Worker routes need the internal token; /ws/hops needs a JWT | architecture, integration |
| REQ-BT-015 | No token 401, bad token 401, missing permission 403 | integration |
| REQ-BT-016 | User reaches only their own farms, /ws/hops included | integration, E2E |
| REQ-BT-017 | Domain has no dependency on Infrastructure; API never calls Cosmos directly | architecture |
| REQ-BT-018 | Exactly one dispatcher reports connected, including during failover | chaos |
| REQ-BT-019 | Active dispatcher killed: another takes over, no event lost, API keeps answering | chaos |
| REQ-BT-020 | Service Bus emulator restarted: every event still completes | chaos |
| REQ-BT-021 | Worker outage does not dead-letter good events | chaos |
| REQ-BT-022 | API scales by KEDA on request rate, target 20. Workers scale on new batches, target 6.7 batches/s a pod: below 3 batches/s 2 Workers, over 30 batches/s 10, read every 10 s at most | load |
| REQ-BT-023 | node2 holds only worker-node2, its netprobe and at most one dispatcher pod from the app | smoke |
| REQ-BT-024 | Every pod and container limit equals caps.json | smoke |
| REQ-BT-025 | Setup refuses a node sum over its cap | smoke |
| REQ-BT-026 | Monitor and dashboard show caps, usage and breaches | smoke, E2E |
| REQ-BT-027 | Monitor sees pod path failure | chaos |
| REQ-BT-028 | Dashboard shows real hops as moving dots for one trace | E2E |
| REQ-BT-029 | RealTime Monitor on is live, off is playback with speed control | component, E2E |
| REQ-BT-030 | Lost socket shows lost state and last data time, never stale green | component, E2E |
| REQ-BT-031 | Production line starts, changes rate from 0 to 35 batches per second, stops from the UI; the server refuses a rate above 35 | E2E |
| REQ-BT-032 | No polling in the browser | E2E |
| REQ-BT-033 | Every colour comes from tokens; text pairs meet WCAG AA, light and dark | unit |
| REQ-BT-034 | Every design system component is in the catalog with all states | component |
| REQ-BT-035 | Pages pass axe, keyboard reachable, usable at 360 px and 1440 px | E2E |
| REQ-BT-036 | Every empty, loading and error state is designed | component |
| REQ-BT-037 | Helm chart lints, renders, rolls back | smoke |
| REQ-BT-038 | A job made from the nightly CronJob completes and flags overdue batches | smoke |
| REQ-BT-039 | Monitor reports each page and each browser path through the ingress as its own check | smoke |
| REQ-BT-040 | Any console error fails every front-end test | E2E |
| REQ-BT-041 | Dashboard node list shows every machine in the cluster, control plane included | smoke |
| REQ-BT-042 | Every infrastructure change appears on the dashboard as it happens: node, pod, replica, rollout, check state, cap, setup step | smoke |
| REQ-BT-043 | While a cluster operation runs, every page shows a persistent indicator with a spinner, until it ends | E2E |
| REQ-BT-044 | Monitor proves real UDP and TCP between pods on every pair of nodes, cluster DNS included, and shows each failure as its own check | smoke |
| REQ-BT-045 | Dashboard Errors counter opens Logs filtered to errors; each entry shows exact reason, step, batch and details | integration, component, E2E |
| REQ-BT-046 | Dashboard shows totals and average per minute for events created and passed, batches, doses and outcomes, with charts per bucket; bucket selector 1m, 30m, 1h, 3h, 1d, at most 120 buckets, remembered per browser | integration, component, E2E |
| REQ-BT-047 | No token in any URL or log; session is an HttpOnly cookie; every security finding has a failing-first attack test, run against the live cluster and the release gate | integration, E2E, smoke |
| REQ-BT-048 | Every endpoint of every service is tested for every role; an endpoint with no row in the table fails the build | architecture, integration, smoke |
| REQ-BT-049 | Repeated unauthenticated requests from one client get growing delays, then 429 with Retry-After; signed-in users and health routes are never delayed | unit, integration, smoke |
| REQ-BT-050 | Every route and socket is rate limited with 429, Retry-After and a problem body; every client and service-to-service call retries transient failures with growing delay and jitter, at most 10 attempts, and never repeats a write | unit, integration, E2E, smoke, load |
| REQ-BT-051 | Real secret values live only in gitignored `deploy/app/.env`, with key names in committed `.env.example`; setup makes the Kubernetes Secret from that file; pods read it through envFrom, never values.yaml or a ConfigMap; every service stops at startup on a missing required setting; gitleaks blocks secrets in pre-commit and the release gate | integration, smoke |
| REQ-BT-052 | Every request and every background action has a correlation ID, taken from a valid `X-Correlation-Id` or generated at the edge, returned on every response and problem body, and carried through events, hops, every service's log lines and the Logs page, where it is a filter | unit, integration, component, E2E |
| REQ-BT-053 | Every error shown says in plain words what happened, where, what it did to the batch and what to do next, with the correlation ID; exception and stack detail is secondary and folded | unit, integration, component, E2E |
| REQ-BT-054 | A cluster operation shows its full step list as checkboxes from its start, ticks each step as it finishes, marks the running and failed step, and shows an ETA taken from earlier runs of the same work, or says none exists | unit, component, smoke |
| REQ-BT-055 | Every role lands on its own dashboard at `/dashboard`, tailored to its visibility and scope, reading only what that role may read; no page tells a signed-in user to sign in as someone else | integration, component, E2E |
| REQ-BT-056 | A browser failure (console error, failed socket, failed request, render fault) is reported with an ID, reconciled against ingress and service logs by that ID, and shown on the Logs page | unit, integration, component, E2E |
| REQ-BT-057 | Every source file and method stays within the limits in `docs/architecture/code-rules.md`; a file or method over its limit fails the build | architecture, unit |
| REQ-BT-058 | The owner hears, with the site down, when a drive holding the cluster runs low, the WSL disk goes read-only or logs I/O errors, Docker or the k3s API goes down, or Monitor dies; these checks run outside Docker, and setup refuses a build when free space is under what a build needs | unit, smoke |
| REQ-BT-059 | Each stage of an event (API call, store write, queue wait, Worker handling) is timed into Prometheus, failures included, and p50, p95 and the peak of events in progress reach the Performance view per bucket, down to 10 s; queue wait is measured so a difference between node clocks cannot enter | unit, integration, smoke |
