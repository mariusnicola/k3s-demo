# Monitoring and caps


## Monitor report
`GET /health` on `127.0.0.1:5320` returns the report, 200 when healthy, 503 otherwise. On `<HOST_IP>:5320` it returns the same status with only `healthy` and `checkedAt`: the two bindings go to different container ports, and only the loopback one serves the report. WebSocket `/ws` stays open to every visitor, and the frames depend on the `bt_session` cookie read at the handshake. `operations` frames go to every socket, because the banner is public. The report and the `changes` and `change` frames go only to a session with `ops.admin`, checked with `JWT_PUBLIC_KEY` the way Live checks it: signature, ES256, issuer, audience, expiry with 30 s skew, subject. A missing or bad cookie, a session without `ops.admin`, or a cookie with no Origin opens an anonymous socket, not a refusal. A cookie with no Origin gets less, not a 403: browsers always send Origin on a socket, so only a script arrives that way. A socket is refused with 429, `Retry-After` and a problem body past 10 open per client, 100 open in total, or 5 upgrades a second with a burst of 10 per client. The client is the connection address, unless the connection comes from a network in `TRUSTED_PROXIES` (comma list, unset trusts nobody), then the last `X-Forwarded-For` entry. A bad entry stops the start. `/alive` and `/health` allow 20 requests a second per client with a burst of 40, then the same 429. Not measured under load: the fastest repeating caller is setup, one request every 2 s, and a smoke run sends its requests one after another. The peer of a loopback caller is the Docker gateway, so every local caller shares one count. A token already past `exp` at the handshake, even inside the skew, opens as a visitor, so a reconnect is not closed again. When an admin session expires while connected the socket closes 4401 `unauthorized`, as Live does. A socket keeps its session until `exp`, up to 8 hours: sign-out only clears the cookie and nothing is revoked on the server, so a copied token still opens an admin socket until then. A token in the URL or a bearer gives nothing. An admin socket gets the report on connect, after each 30 s cycle, and after any cluster change (nodes, pods and caps refreshed, checks unchanged with their own `checkedAt`); a socket from an origin other than the ingress gets 403. The browser reaches only `/ws`, through the ingress, see `docs/architecture/overview.md`. `/health` is not exposed.
```json
{
  "checkedAt": "", "healthy": true,
  "checks": [{ "name": "", "healthy": true, "error": null }],
  "nodes": [{ "name": "", "role": "control plane|worker", "ready": true, "allocatableMemoryMi": 0, "cpuUsageMilli": 0, "memoryUsageMi": 0 }],
  "pods": [{ "namespace": "", "name": "", "node": "", "phase": "", "ready": true, "cpuLimitMilli": 0, "memoryLimitMi": 0, "cpuUsageMilli": 0, "memoryUsageMi": 0, "terminating": false, "busy": true }],
  "workers": { "max": 0 },
  "dispatcherLeader": "",
  "store": "mongo|cosmos",
  "caps": [{ "node": "", "resource": "cpuMilli|memoryMi", "cap": 0, "sum": 0, "breach": false }],
  "capIssues": ["text"]
}
```
- `pods` covers `kube-system`, `metallb-system`, `ingress-nginx`, `prometheus`, `keda`, `batch-tracker`: every phase but Succeeded, so a Failed pod shows. `terminating` is true from the delete until the pod is gone, the same moment as the feed's `pod deleted`: the row keeps its limits, and caps do not count it. `ready` is true when every container is ready. Caps count Running and Pending pods only.
- Usage is null when metrics-server does not answer. The rest of the report still arrives.
- `nodes` is the control plane plus every Kubernetes node. The control plane is no Kubernetes node: its name comes from `CONTROL_PLANE_NAME`, `ready` means the API answered this cycle, usage comes from `/proc` of the WSL VM, `allocatableMemoryMi` is null. The row stays when the API is down.
- `nodes`, `pods`, `caps`, `capIssues` and `dispatcherLeader` follow a watched change on the next 1 s tick plus one cluster read, and a report keeps the newest read. A read that failed changes nothing on the map until the cycle reports it.
- `store`: `BATCH_STORE` from the `batch-tracker-config` ConfigMap, which the API pods take by `envFrom`, null when unreadable. A pod started before a ConfigMap change keeps the old value until its rollout ends.
- `dispatcherLeader`: the pod holding the `dispatcher` Lease, null when the lease is released, past its time or unreadable. Read on each cluster read: a leader change with no pod change shows on the next 30 s cycle.
- `busy`: true when the Worker pod passed an event in the last minute or has work in flight now: per pod, `rate(bt_events_passed_total[1m]) > 0` or `bt_worker_work_in_flight > 0`, as of its last Prometheus scrape, every 15 s. False only when both read zero. Null when unknown: either read failed or has no value for that pod and the other does not say true, Prometheus is down or stuck past 5 s, or the pod is new. The rate needs two scrapes, so a new pod reads null for up to 30 s. Null on every pod that is no Worker. The gauge is what shows a nightly run, which passes no event. A refused event counts for neither unless it is in flight. CPU use plays no part.
- A Worker added or removed reaches the page by the pod watch: the next 1 s tick plus one cluster read. `busy` follows the 15 s scrape instead.
- `workers.max`: the most Worker pods that can run, every `caps.json` entry for the `worker` container in the app namespace added together, so the scaled Workers and the fixed one on node2. The whole object is null when the read failed or the file has no such entry.
- `capIssues`: a live limit differing from the file, a pod on the wrong node, more pods than the file allows, a pod not in the file, a pod with no limit.

## Change feed
`/ws` sends other frames beside the report. A frame with `type` is not a report.
- `{type:"changes",changes:[...]}` on connect, the last 500 in memory. `{type:"change",change:{time,kind,object,old,new}}` as each happens.
- Kubernetes list then watch, and a reconnect compares against what was known: nodes added, removed, Ready; pods created, scheduled, restarted, failed, deleted; Deployment replicas, ready count, image; KEDA desired replicas; Helm release status from Secret labels, never the Secret data.
- From each report: check healthy to failing and back, cap sum changes.
- Work Kubernetes cannot see: scripts dot-source `deploy/changes.ps1`, which appends JSON lines to `deploy/.changes/changes.jsonl`. The monitor mounts the folder read-only and tails it each second, from the start after a restart. No network endpoint is added.

## Cluster operations
`{type:"operations",operations:[{id,name,work,step,startedAt,lastSeenAt,state,endedAt,steps:[{name,state,startedAt,endedAt}],etaSeconds}]}` on connect and on every state change.
- `steps`: the full ordered list a start line declares, in that order from the start. Step state is `pending`, `running`, `done` or `failed`. A new step line marks the one before it `done`. A failed end marks the running step `failed`, and later steps stay `pending`. A start line with no list gives `[]`, and `step` alone shows progress.
- `etaSeconds`: the sum, from the last successful run of the same `work`, of every step not `done`, a running one in full. So it changes when a step ends, not each second. A step with no earlier duration adds 0. Null when no such run exists in the log, when the list is empty, or once the run has ended.
- Sources: `Start-Operation`, `Set-OperationStep`, `Stop-Operation` in `deploy/changes.ps1`, and any Deployment with old pods still present (a rollout; a scale does not count).
- States, decided only in `Operations` in `deploy/monitor/Feed/Operations.cs`: `running`; `no-signal` after 60 s with no heartbeat or watch event; `done`, kept 15 s; `failed`, and `no-signal`, kept until the same name starts again.
- Heartbeat: `Start-Operation` starts a process that writes one every 15 s and ends with its script, so a killed script goes silent.

## Pod network
DaemonSet `netprobe` runs on every node, a tainted node included. Every 10 s each pod resolves headless Service `netprobe` through cluster DNS over UDP, then sends an HTTP request and a UDP echo to every peer. Ping is not enough: ping passed while all pod UDP and TCP from Node1 failed, see `docs/decisions/0003-node1-node-in-docker.md`.
- The monitor reads each pod's `/result` through the API server, so a broken pod network cannot hide its own report.
- `pod dns <node>` and `pod net <from> to <to>` per ordered pair of nodes in `caps.json`. A pod missing, an unreadable result, or a round number unchanged since the last cycle fails them.
- Detection: up to 10 s probe round plus 30 s monitor cycle, 40 s at most on a normal path; a stale round adds one cycle.

## Performance series
`GET /metrics/series?bucket=10s|30s|1m|30m|1h|3h|1d`, same origin through the ingress. It needs the same `ops.admin` session cookie as the report: anyone else gets 401, checked before the bucket. Any other bucket answers 400. Limited as `/health` is. At most 120 buckets, oldest first, ending at the last whole bucket.
```json
{
  "bucket": "1m", "available": true, "error": null, "historyStart": "2026-10-31T06:00:00Z",
  "series": [{ "name": "", "total": 0, "averagePerMinute": 0, "points": [{ "at": "", "value": 0 }] }],
  "stages": [{ "stage": "api", "points": [{ "at": "", "p50": 0.012, "p95": 0.084 }] }],
  "pending": [{ "at": "", "value": 0 }]
}
```
- `stages` always holds `api`, `store`, `queue`, `worker`, `bundle` in that order, each with 120 points on the same `at` as `series`. `p50` and `p95` are seconds, interpolated inside a histogram bucket, null with no samples, never 0. The histogram stops at 60 s, so 60 means 60 s or more.
- `pending` is the peak of `bt_events_pending` in the bucket, on the same `at`. Null with no leader counting or a failed count, never 0.
- Prometheus down empties `stages` and `pending` as well.
- `10s` needs the `batch-tracker` scrape job's 5 s and `30s` needs any 15 s scrape: a window needs two samples, and a 15 s window on a 15 s scrape came back empty.
- `value` is the count in that bucket, from `increase()` summed over pods, so a pod restart neither dips the line nor counts twice. `total` is the sum of the points. `averagePerMinute` is `total` divided by the minutes covered from `historyStart` or the first bucket, whichever is later.
- `at` is the bucket start, UTC.
- Prometheus keeps 120 days on a 40 Gi `local-path` volume on k3s-node2, capped at 38 GB by size, so the `1d` bucket can fill. `local-path` does not enforce the 40 Gi, so the size cap is what protects the node's disk. Measured idle at 1-day retention: 3,360 samples a second, about 306 MB a day, so about 37 GB at 120 days, of 68 GB free. 43,000 of the 50,000 series are `kubernetes-api-servers`. The `batch-tracker` job at 5 s, 15 pods: 851 samples a scrape, 170 a second, 11 to 15 MB a day at 0.77 to 1.05 bytes a sample, computed from 109 scraped samples before stage timing and not yet measured after it. Under load a moving histogram compresses worse; past the size cap the oldest days go, the disk stays safe.
- `value` is null for a bucket before `historyStart`, or when Prometheus returned nothing for it. Null means unknown, never zero.
- Prometheus down: 503, `available` false, `error` says why, `series` empty. Never zeros. Monitor check `prometheus` fails too.
- `{type:"metricPoint",at,values:{"<name>":0}}` on `/ws`, admin sockets only, 20 s into each minute so the last scrape has landed: the bucket just closed, at 1m, with `at` the start of that minute. A metric with no value is left out, and Prometheus down sends no frame. A wider view adds it to its open bucket.
- `{type:"stagePoint",at,stages:[{stage,p50,p95}],pending,values:{"<name>":0}}` on `/ws`, admin sockets only, 6 s past each 10 s so the last scrape has landed: the 10 s bucket just closed, with `at` its start. It is that bucket of `/metrics/series?bucket=10s`, read by the same code: the four stages in order, `pending` the peak, `values` the count of every `series[].name`, each null when unknown and never left out. No admin socket open means no frame and no Prometheus read. Prometheus down sends no frame.
- `series[].name` values are the metric names the app exports. The monitor reads Prometheus through the API server, so no port is published.

### Metric names
Counters in Prometheus text on `GET /metrics` of the service named, never routed by the ingress. Prometheus finds API, Worker and Dispatcher pods in its own `batch-tracker` job, by the `batch-tracker/scrape` annotation, port 8080, every 5 s. They do not carry `prometheus.io/scrape`, or the chart's `kubernetes-pods` job would scrape them too and every sum would read double. A request the Worker route forwards carries `X-Forwarded-For` and answers 404. Label `farm` on each. A counter rises once, after its write succeeded, so a redelivery, a failed write or a lost If-Match adds nothing.
| `series[].name` | Counts | Service |
|---|---|---|
| `bt_events_created_total` | Events written with `pending` | API |
| `bt_events_passed_total` | Worker wrote the step record | Worker |
| `bt_events_dead_lettered_total` | Events the Worker refused, row now `dead` | Dispatcher |
| `bt_events_replayed_total` | Replays of a blocked batch | API |
| `bt_batches_created_total` | Batches created | API |
| `bt_dose1_total` | Moves to Dose1 | API |
| `bt_dose2_total` | Moves to Dose2 | API |
| `bt_inspections_passed_total` | Inspections passed | API |
| `bt_inspections_failed_total` | Inspections failed | API |
| `bt_batches_shipped_total` | Moves to Shipped | API |
| `bt_batches_closed_total` | Moves to Closed | API |
| `bt_batches_rejected_total` | Moves to Rejected | API |
| `bt_bundle_items_total` | Items taken by `POST /api/batches/bundle` | API |
| `bt_bundle_item_failures_total` | Those refused, each with its status on its result | API |

The Worker also exports the gauge `bt_worker_work_in_flight`, no `farm` label, starting at 0: `POST /events` and `POST /nightly/run` running now, past sign-in and limits. It is no series on the Dashboard; the report's `busy` reads it beside the rate of `bt_events_passed_total`.

`bt_events_pending` is a gauge on the leading Dispatcher only: rows with `pending.status` pending or sent, read from the store every 5 s beside the sweep, so a Worker outage does not freeze it. A standby exports no such line, so a gap reads as unknown.

`bt_stage_seconds` is a histogram, label `stage`, buckets 1, 2.5, 5, 10, 25, 50, 100, 250, 500 ms, 1, 2.5, 5, 10, 30, 60 s and `+Inf`. A failure is timed like a success.
| `stage` | Time | Service |
|---|---|---|
| `api` | A call to `/api/*`, refused ones included, not `/api/healthz` or `/api/ready*` | API |
| `store` | One create or replace, or one list write, a lost If-Match or a failure included | API, Worker, Dispatcher |
| `queue` | Stored as pending to the Worker reading it | Worker |
| `worker` | `POST /events` handling, refused and failed included | Worker |
| `bundle` | One bundle, read, write and news. Its count is bundles, so items per bundle is `rate(bt_bundle_items_total)` over it | API |

`queue` subtracts two readings of one clock, the store's: `pending.storedAt` is stamped from it on the first write of a pending event, and the Worker reads it again on receipt. Each pod reads the store's clock at most once per 30 s (`hello` on Mongo, `GetCurrentTimestamp` on Cosmos), places the answer at the middle of its round trip, and carries it on with its monotonic timer, so a difference between node clocks cannot enter. The error is at most half each pod's last round trip; a result below zero reads 0. A redelivery with `pending` still set counts again, with its longer wait. A replay starts a new wait. An event stored before this stamp existed has no `queue` time.

## Ingress log
Every request through the ingress writes `{time,id,status,upstream,path}`: `id` is the `X-Correlation-Id` the request sent, `upstream` is nginx's `$upstream_status`, `path` has no query. Nothing else goes in, no cookie, token or address.

`GET /ingress/logs?correlationId=<id>`, same origin through the ingress, same `ops.admin` session cookie as the report: anyone else gets 401, checked before the id. A missing or malformed id answers 400. Limited as `/health` is.
```json
{ "correlationId": "", "available": true, "since": "2026-11-01T10:00:00Z", "rows": [{ "at": "", "status": 0, "upstreamStatus": "", "path": "" }] }
```
- `rows` are newest first. The monitor follows the controller's log and holds the last 5,000 rows with an id, in memory, so a restart empties it until the first tail.
- `since` is the oldest time held. Before it, no row means unknown.
- Not following the log: 503, `available` false, `rows` empty. Never an empty list, which would read as never arrived.
- A row shows a request with this id arrived, and the status it got.
- `upstreamStatus` is what nginx records, as text: `-` when no upstream answered, `502` for one answer, `502, 200` when nginx tried more than one. Null on a row written before the field.
- With `-`, the status is nginx's own. nginx also records 502 and 504 for a try that failed to connect or timed out, so those do not prove a service answered. Null says nothing either way.
- The service log also holds only warnings and errors, so no service row proves nothing about a quiet answer. Only `upstreamStatus` can say a request stopped at the ingress: `-` in every group of every row for the id, and no service row.
- No row means it never arrived only if every log was read in full and the request is inside what the log holds. A failed, filtered or short read, or a report near `since`, means unknown.
- A client writes the id, so a row shows that a request claimed it, nothing more.

## Monitor checks
- `control plane`: the API answered this cycle.
- Nodes from `caps.json`, workloads per namespace, ingress address, Service Bus and Mongo round trips, plus Cosmos unless `store` reads `mongo`, so Mongo live calls no Cosmos. `mongo` signs in as user `monitor`, limited to database `monitor`, and reports the failure's type alone, because the driver's text can hold the connection.
- `workloads <namespace>`: a DaemonSet fails below desired. A Deployment fails at once at spec 0 or below its minimum: the HPA's `minReplicas`, or spec with no HPA. Between minimum and spec is a scale-up, which fails after 240 s below spec, and reaching spec starts the 240 s again. 240 s sits above the slowest Worker start read, 187 s, one read after a node restart.
- `caps`: fails on any breach or `capIssues` entry.
- `prometheus`: Prometheus answers through the API server.
- Cadvisor: the chart's `kubernetes-nodes-cadvisor` job dials each node's internal address. Node1's is Docker-internal, 198.19.25.2, so that target stays down from node2. `kubernetes-nodes-cadvisor-external` reads Node1 at its external address.
- `workloads batch-tracker`, `app api ready` and the page and ingress checks below: always run, so with the namespace gone they read red. `app api ready` calls `/api/ready/deep` through the Kubernetes API proxy, so the path to the pod and from the pod to its store and Service Bus is covered, which a check from WSL cannot see.
- `page <route>` for `/`, `/signin`, `/alerts`, `/dashboard`, `/design-system`: GET through `<INGRESS_IP>` returns 200 HTML, and every JS and CSS file it names returns 200 and is not HTML.
- `ingress /ws`: opens with no session and its first frame must be `operations`. An admin's first frame is the report, so a report reaching an anonymous socket fails here. The monitor holds no `ops.admin`, so it cannot prove a report reaches a browser: that is the `ada` smoke rows. `ingress /line`: exactly 403 with the monitor's machine token. `ingress /ws/hops`: exactly 101 on an upgrade with that token. The `monitor` identity has no permissions, is signed in through `POST /api/auth/machine` with `MONITOR_MACHINE_TOKEN`, and reuses its token until one minute before expiry. Authenticated requests never reach the failure ledger. A lost route falls through to web and answers 200, which the check reports as `route dropped`; a refused sign-in reads `machine sign-in refused`. Anonymous 401 on these paths is smoke only (`check-69`).
- `line bundles`: The Line is outside Prometheus, so a POST that never reached the API counts nowhere there. Reads `GET /line/bundles` through the ingress with the machine token. Fails when `failing` holds, naming the failure, or on any answer but 200. `failing`: the last POST failed, none has worked since, under 120 s ago, so a stopped Line ages out. An idle Line passes.
- `nightly job`: Fails when the CronJob is missing or suspended, when its newest run failed, or when the last run is over 25 h old.
- `dispatcher sweep`: A renewing lease proves only the election, so this reads the holder's `/status` through the Kubernetes API proxy. Fails when no live holder can be read, when `/status` does not answer, when the holder is not leading, and when its last sweep is over 60 s old (idle sweeps run at most 4 s apart). Monitor's Mongo user cannot read batches, so it fails whether rows are pending or not. A Worker outage pauses the sweep, so it reads failing for that outage too.
- `monitor loops`: names every background loop whose last cycle failed or that ended, with the reason. A failed report cycle publishes the last report with this check failing, dated now. Each failure logs `[ERROR]`, the loop retries after 5 s, and every loop logs when it ends. A cycle that returns clears its failure. Following loops differ, because returning proves nothing there. `ingress log` stays connected and never returns, so the first line it receives clears it: connected with nothing received stays failing, and a stream that breaks fails again. A `watch <kind>` logs `[WARN]`, reads failing, and retries its list after 2 s; the next list that answers clears it, so a quiet watch needs no event. A watch timeout reconnects and never reads failing, the quiet one included: with no event on the connection the client throws `EndOfStreamException` while opening, and only that shape is let through. Any other close reads failing until the next list, about 2 s. Once a list has answered, a watch whose stream keeps failing clears for the moment before each new failure.
- `host`: reads `/changes/host.json` from the machine watch. Fails on any failing check there, naming each, and when the file is missing, unreadable, empty of checks or over 180 s old, which means the watch died. So each side sees the other go.
- The first report after a start has empty `checks` and `healthy` false. It exists so `/health` and an admin's socket have a report before the first checks end.
- Docker containers: limits checked by smoke only. The monitor has no Docker socket.

## Machine watch
Monitor runs in Docker, so a full drive, a dead WSL or a dead Docker takes it down with no word. `deploy/watch.ps1` watches from Windows: task `BatchTracker-watch`, every minute, as the owner. It only reads, and never calls WSL, so it cannot start WSL on a sick disk.
| Check | Reads | Fails on |
|---|---|---|
| `disk C:`, `disk <drive>` | Free space on C: and on the drive holding the vhdx, found from the registry (`BasePath` under `Lxss`), so a moved disk stays watched | Under 35 GB, or unreadable |
| `wsl disk` | `.changes/wsl.json` | `ro` or `emergency_ro` on the root mount, a disk error in the kernel log, or the file missing, broken, incomplete or over 180 s old |
| `docker` | `.changes/wsl.json` | `docker info` failed, or the file as above |
| `service bus throttle` | `.changes/wsl.json` | `busThrottled` above 0: the bus emulator logged error 50002 in the last 60 s. Also the file missing, broken, without the count or over 180 s old |
| `k3s api` | `https://127.0.0.1:6443/readyz` from Windows, 3 s limit. Loopback, because the LAN address from Windows itself can hit a stale portproxy | No answer or any status but 200, 401, 403. 401 and 403 come from the API server itself |
| `monitor` | `http://127.0.0.1:5320/alive` from Windows, 3 s limit | No answer or any status but 200 |

- `.changes/wsl.json`: `batchtracker-wsl-report.timer` in the distro writes `{at,rootOptions,ioErrors,docker,busThrottled}` every 30 s. It lands on the Windows disk, so a read-only root can still report. The error count lasts until the VM restarts.
- `.changes/host.json`: `{checkedAt,checks:[{name,healthy,error}]}`, one report, replaced on each run.
- Owner with the site down: a `host check` change line, once per new failure and once per recovery, which reaches the feed once Monitor is back. The watch shows no popup or dialog on the desktop. A full drive stops the `host.json` write, so the same change line repeats each minute.
- Setup refuses a build under 18 GB on C:, the vhdx drive or the `%TEMP%` drive, before it writes anything.
- Both figures are interim and live in `deploy/watch.ps1`:
  - 18 GB: build cache 17.9 GB, `docker system df`, 2026-10-31 10:53 UTC. That is every build to that date, not one, and assumes no prune came first. `%TEMP%` gate work has no measure. `Write-BuildSpace` logs vhdx size and drive free around the gate builds and the image builds, as `build space` change lines. A real build's gap replaces this figure. An ext4 block freed earlier is reused with no vhdx growth, so a gap can read low.
  - 35 GB: 24 h at 1.43 GB/h. ext4 went from 28G on 2026-10-30 19:51 UTC to 98G on 2026-11-01 20:37 UTC, all work together. The 24 h is a choice, not a measure.

## Caps
`deploy/caps.json` holds node caps, every pod and every Docker container: CPU and memory, plus the Helm value path for charts. Setup writes Helm values to `deploy/.generated` and compose values to each compose folder's `.env`. Setup stops if a node sum exceeds its cap, if memory requests exceed live allocatable, or if an entry is missing. Smoke compares live limits to the file. Sum uses limits at max replicas. k3s add-ons (CoreDNS, metrics-server, local-path) are not Helm values here, so they are listed as fixed reservations and not compared to the file. Infrastructure pods are pinned to the node in the file. CPU at `k3s-node1` is the tight sum, 4000m of 4000m with 0m free, the transient `nightly` pod included; a rolling update surges one pod over it for a short time. At ten Workers the node2 limits sum to 3904 Mi, over the 3840 Mi the node reports allocatable, so they hold only while not every pod reaches its limit at once. Requests, 2128 Mi, fit.
Node size: Hyper-V dynamic memory means a node reports less than its cap. Requests must fit allocatable, and limits fit the cap. Fixed sizes need the owner, see `docs/decisions/0002-batch-tracker-scope.md`.

## Limit choices
Before the app existed, the monitor image (same SDK stack as the dispatcher) at 300m and 192 MiB with workstation GC peaked at 107 MiB and took 3.7 CPU-seconds to settle. Idle figures below are from the installed app, 2026-10-31, no load. Every .NET pod sets `DOTNET_gcServer=0`. Load figures come from the load test.
| Entry | CPU | Memory | Reason |
|---|---|---|---|
| api x2 | 350m | 256Mi | Takes the most requests, idle 109 MiB. Request equals limit so KEDA's 70% reads as 70% of the limit |
| worker x5 | 380m | 256Mi | Request 200m on both Worker entries, so 70% reads the same on each |
| worker-node2 x5 | 380m | 256Mi | Same as worker, so the two scale evenly. Threshold 1 on both objects. Each reads the whole route, so both ask for the same count: 1 request/s on the route asks for one pod on each node |
| dispatcher x2 | 300m | 192Mi | Idle 61 to 94 MiB. At 225m the leader sat at its limit through each wave under the Line |
| dispatcher-node2 | 300m | 192Mi | node2 has spare CPU |
| live | 300m | 512Mi | No SDK, holds 500 hops and every batch row a store scan returns; 128Mi was OOMKilled. At 100m it restarted in a loop under the Line |
| web | 50m | 48Mi | nginx, idle 13 MiB |
| nightly | 50m | 32Mi | One curl, a few seconds; job completes inside the limit |
| netprobe | 50m | 64Mi | One per node, one small round every 10 s |
| ingress-nginx | 1000m | 256Mi | On node2, where CPU is spare. The CPU limit took it from 219 MiB to 30 MiB idle |
| prometheus-server | 500m | 768Mi | Was 1Gi. Measured 358 MiB, 2x with room for app series |
| prometheus-reload | 20m | 32Mi | Measured 7 MiB |
| keda-operator, keda-metrics-apiserver | 200m | 128Mi | Were 1 CPU, 1000 Mi. Measured 30 MiB |
| keda-webhooks | 100m | 64Mi | Were 1 CPU, 1000 Mi. Measured 10 MiB |
| metallb-controller | 100m | 64Mi | Moved to node2. Measured 19 MiB |
| metallb-speaker | 200m | 128Mi | One per node. Crash loop cause unknown; 128 Mi is headroom for measured reclaim, not its fix: 89.6 MiB binary plus 18.2 MiB peak anon is 107.8 MiB, plus 20 MiB room. Stale pod after a `batchtracker-node1` restart, 200m, 65 s read: 66.7 MB held at the limit, 6.77 M pages scanned, 4.53 M refaults, 7560 major faults, 13.49 of 13.96 s CPU in system, killed by its probe at 35 to 40 s. New pod with its 93,952,846 byte binary's cache dropped: ready at 12 s, 0 restarts at 2m17s, 304,676 refaults, 640 major faults, 2.28 s CPU in 177 s, also at the limit, and still reclaiming at 30 minutes: 540,623 refaults, no restart. Neither limit is shown to fix the loop |
| ingress-nginx-create, -patch | 50m | 32Mi | Short jobs at chart upgrade |
| reserved os-k3s-agent | 100m Node1, 200m node2 | 512Mi Node1, 768Mi node2 | Node usage minus pods measured 455 MiB and 670 MiB on earlier workers. Also counted as a memory request, so pods and the system fit allocatable together |
| reserved coredns, metrics-server, local-path | 100m, 100m, 50m | 170Mi, 100Mi, 32Mi | Add-on values or measured use, on the node each runs on today. coredns is not pinned. Setup pins metrics-server with a patch, since a k3s add-on has no chart value |
| batchtracker-cosmos | 1500m | 2048Mi | Docker CPU limits in `deploy/caps.json` sum to 13 CPUs, past WSL's 12, so it can compete under load |
| batchtracker-servicebus | 500m | 1024Mi | Idle under 1% |
| batchtracker-servicebus-sql | 1000m | 2048Mi | Measured 1.35 GiB |
| batchtracker-monitor | 500m | 256Mi | Measured 147 MiB |
| batchtracker-line | 500m | 192Mi | Idle 36 MiB, generates load when running |
