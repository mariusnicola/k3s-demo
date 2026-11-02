# API


## API
All under `/api`, JSON. Policy on every endpoint, fallback denies.
| Route | Permission |
|---|---|
| `POST /auth/dev-login {user}` | anonymous, 404 unless `DEV_LOGIN_ENABLED` is true. Sets the cookie, reply `{ user, permissions, farms }`, no token |
| `POST /auth/logout` | any signed in, 204, clears the cookie |
| `POST /auth/machine` | anonymous, header `X-Machine-Token`, reply `{ token }` for the Line |
| `GET /me` | any signed in |
| `GET /session` | anonymous, 200 with `{ "me": <the body of GET /me> }`, or `{ "me": null }` with no session or a refused one. Counts no failure. Under the anonymous rate limit |
| `GET /batches?state=&farmId=&limit=` | batch.read, own farms |
| `POST /batches {farmId, product}` | batch.write + farm. Optional header `Idempotency-Key` |
| `GET /batches/{farmId}/{id}` | batch.read + farm |
| `POST /batches/bundle {items}` | batch.write. Up to 500 creates, advances and inspects, one result each, in order, see `docs/plans/line-bundles.md` :: Request. Each item passes its single route's checks; inspect also needs batch.inspect. Always 200 unless the list is refused, 400, or the store fails, 500 |
| `POST /batches/{farmId}/{id}/advance {to}` | batch.write + farm, If-Match. `to` Inspected is refused 409, only inspect reaches it |
| `POST /batches/{farmId}/{id}/inspect {passed, reason}` | batch.inspect + farm, If-Match |
| `POST /batches/{farmId}/{id}/fail-next` | ops.admin |
| `POST /batches/{farmId}/{id}/replay` | ops.admin, 409 unless Blocked |
| `GET /summary` | batch.read, own farms: counts by state, pending, blocked, flagged. A flag on a Closed or Rejected batch is not counted |
| `GET /alerts` | batch.read, own farms. A flag on a Closed or Rejected batch is not listed. It stays on the batch: `GET /batches/{id}` still shows it, and nothing deletes a flag |
| `POST /compliance/run` | ops.admin, calls Worker `/nightly/run` |
| `POST /browser-reports` | Anonymous, a browser failure for the service log. Contract in `docs/architecture/events.md` :: Browser reports |
| `GET /healthz` | anonymous, process up, liveness probe |
| `GET /ready` | anonymous, readiness probe. 200 proves this pod is not stopping and finished a store read within 10 s, no more than 15 s ago. A background read every 2 s holds the answer, so a slow store cannot race the probe's 3 s timeout. All three numbers are unmeasured |
| `GET /ready/deep` | anonymous, the same store answer as `db`, `dbMs` (last good read in ms, null before one) and Service Bus TCP from the pod, for the monitor, 1 a second per pod. A store at 3 to 10 s reads good, and `dbMs` shows it |

`GET /metrics` at the root of API, Worker and Dispatcher: anonymous, Prometheus text, for a direct scrape. 404 with `X-Forwarded-For`, so the ingress never serves it, Worker host included.

Status: 201 create with ETag, 400 validation, 401, 403, 404, 409 illegal or pending, 412 stale, 415 not JSON, 422 `Idempotency-Key` reused with another body, 428 no If-Match, 429 over a limit. Errors are RFC 7807. `product` is limited to 200 characters, `reason` to 1000, `Idempotency-Key` to 200.

Demo users: `olivia` operator farm-a farm-b (batch.read, batch.write), `ian` inspector all farms (batch.read, batch.inspect), `ada` admin all farms (all). `viewer` read only farm-a.
JWT ES256, 8 h expiry. Only the API holds `JWT_PRIVATE_KEY`; API, Live and Line verify with `JWT_PUBLIC_KEY`. Both are one line of base64: PKCS#8 and SubjectPublicKeyInfo, P-256.

## Sign in
- Browser: cookie `bt_session`, HttpOnly, SameSite Strict, Path `/`, Max-Age 28800, Secure unless `COOKIE_SECURE` is false. No script reads it.
- A bearer header wins over the cookie. A bad bearer is 401, it never falls back. A token in the URL is ignored, and the hosting log, which would record it, is filtered in every service. The routing, result and HTTP client lines per request are written only from Warning up, for what they cost a pod at its CPU limit. In their place every service writes one Information line, `Request <method> <route pattern>`, or `no route` when nothing matched, inside the correlation id's scope. It holds the pattern, never the URL. Routes marked `LimitedAsHealth` write none: `/healthz`, `/ready`, `/metrics`, `/status`.
- Line: signs in at `/auth/machine` with `LINE_MACHINE_TOKEN`, then sends a bearer. Caller `line`: batch.read, batch.write, batch.inspect, all farms, no ops.admin. It needs no dev-login.
- Monitor: signs in at `/auth/machine` with `MONITOR_MACHINE_TOKEN`, then sends a bearer. Caller `monitor`: no permissions, no farms, so `/line` answers 403 and `/ws/hops` upgrades and shows nothing.
- Origin: every non-GET in API, Live and Line needs an Origin in `ALLOWED_ORIGINS` (comma separated). No Origin passes only when the request carries no cookie, or carries a bearer. Refused: 403 RFC 7807. Sockets follow the same rule.
- Logout clears the cookie in that browser. A copied cookie works until it expires, and an open socket stays open until its token expires.

## Correlation
Every request and background action has one `correlationId`.
- `X-Correlation-Id` is taken only when one header matches `[A-Za-z0-9-]{1,64}`. Anything else, two headers included, gets a generated id and the request goes on.
- Every response carries it in `X-Correlation-Id`, and every problem body as `correlationId`. Every status with no body of its own gets a problem body, an empty 401 included.
- Every call to another service sends it. Every log line written during the action carries it as a log scope, `correlationId:<id>` on a single line.
- Background: the sweep, dead letters and a Worker delivery reuse the id on the event; the reconcile and a dispatcher loop turn take a new one per run; the nightly run uses the id of its request; the Line takes one per batch walk.
- A replay gets the id of the replay request and keeps the old one in `pending.replayOf`.
- `traceId` stays per step and `correlationId` links the action. It sits on the pending event and not on a step, so the hash chain is unchanged.
- `traceparent` and `baggage` (holding `correlationId`) also leave on every HTTP call, the Cosmos SDK's included.

## Errors
Every problem body, on every service, adds to RFC 7807:
- `detail`: what happened. A route's own plain text, or a fixed text per status. At 500 and above always the fixed text.
- `where`: what the person was doing, in words, such as `Moving a batch on farm-a`. A farm the service does not know reads `a farm`.
- `path`: method and path, secondary.
- `impact`: what it did to the batch, one of: no batch was affected, only a read, not changed, may have changed, some batches may have been flagged (a compliance or nightly run at 500 and above).
- `next`: what to do, a fixed text per status. A 429 says the wait in seconds, from the same value as `Retry-After`.
- `correlationId`.

The body is `application/problem+json` whatever `Accept` the request sent.

A body holds no exception text, type or stack. These stay in the pod log with the id, which masks bearer tokens, JWTs, `AccountKey=` and `SharedAccessKey=` values.

## Limits
Token bucket per pod, in API, Live, Line, Worker and Dispatcher. Constants in `RateRules`.
| Caller | Rate a second | Burst |
|---|---|---|
| Anonymous, per client address | 5 | 10 |
| Signed in, per user | 50 | 100 |
| Line machine caller | 500 | 1000 |
| `/api/ready/deep`, one bucket | 1 | 1 |
| `/healthz`, `/api/healthz`, `/ready`, `/api/ready`, `/metrics`, Dispatcher `/status`, per client address, own bucket | 20 | 40 |
| Worker `/events`, `/nightly/run`, Live `/reports`, `/hops`, `/changes`, `/logs`, per client address, own bucket | 500 | 1000 |
| `POST /browser-reports`, per client address, own bucket | 2 | 20 |

- Client address: the connection address, unless the connection comes from a network in `TRUSTED_PROXIES` (comma list, unset trusts nobody), then the last `X-Forwarded-For` entry, which that proxy wrote. A bad entry stops the start. Without the setting every client behind the ingress reads as the ingress and shares one count. Set to k3s-node2's pod network on api and Live, because the ingress pod's address changes on restart, and to k3s-node2's node address on the Line and the monitor, because the ingress reaches their Docker ports from that address. Anything else on k3s-node2 can still write the header.
- Over a limit: 429, `Retry-After` in whole seconds above zero, RFC 7807 body.
- Probes stay far under the health limit: a pod takes at most 0.4 kubelet probes a second and one scrape per 15 s. A 429 there spends no failure.
- A valid `X-Internal-Token` on those Worker and Live routes is never refused: a 429 on `/events` gives the event back to `pending` and three of those stop delivery. Only a caller without it reaches that limit.
- Limits count a pod at a time, so 2 pods allow twice the numbers.

## Failed sign-ins
Every 401 in API, Live and Line counts against the client address, the same address as the limits. Constants in `FailureRules`.
- The first 2 failures have no delay. Then 1 s, doubling to 30 s. The 401 is held, no thread is. Its seat stays taken for the whole delay even if the client hangs up, so leaving early buys no speed. A client that leaves before the route answers still counts, once the route ends in a 401; a route that stops on the cancelled request and answers no 401 does not.
- The count clears only after 15 min with no failure. A sign-in clears nothing, because one address can hold many clients.
- More than 10 requests with no session at once from one client: 429 with `Retry-After`, the route does not run.
- From the 10th failure inside that window: 429 with `Retry-After` set to what is left until the count clears, one request at a time too. The route does not run and the request adds no failure. On a sign-in route (`POST /auth/dev-login`, `POST /auth/machine`) the credential is checked first: a right one passes at once, a wrong one holds its seat for the full delay, then gets the 429 and counts, so guessing runs at 10 seats per 30 s.
- All 10 seats held: 429 for every request with no session, a right sign-in included. A refused client holds its seats, so sign-in from an address it shares waits: stopping guessing comes first.
- A valid session, machine token or internal token is never delayed or counted. Neither are routes open by design: health, readiness, `/metrics`. A bad or missing token on Live `/reports`, `/hops`, `/changes` and `/logs` counts like any failed sign-in. `POST /auth/logout` and `GET /me` with no cookie and no bearer token answer 401 and do not count, so a repeated sign-out or a page asking who is signed in costs nothing; a refused credential there counts.
- `GET /session` is the exception: open by design, it counts nothing, a refused credential included, and answers `{ "me": null }`. It stays under the anonymous rate limit, so a burst gets 429.
- The count is per pod, so with 2 pods a client alternating between them gets each delay after twice as many failures. At most 10000 clients per pod are held: quiet clients leave first, then the fewest failures.

## Retries
One handler, `RetryHandler`, on every service to service HTTP client. Constants in `RetryRules`.
- Retries 429, 502, 503, 504, a timed out attempt and a failed connection. Any other answer returns at once.
- 10 attempts. Delay 200 ms doubling to 30 s, equal jitter. All waits span 40.5 s to 81 s.
- `Retry-After` above the delay replaces it. Past 30 s the retries end and the log says the wait. Malformed: the delay stands.
- Attempts time out at 10 s unless the call sets its own: Worker delivery 10 s, nightly 5 min, reports 1 s.
- After the last attempt the last answer or a failure naming the attempts comes back, and a warning is logged. The Line puts the count in its Logs entry and counts each 429 in `throttled`.
- A POST goes once unless it declares itself safe:
| Call | Why a repeat is safe |
|---|---|
| Line `POST /batches/bundle`, create item | `key`: the batch id comes from caller and key, so the store refuses a second write. Same key and body returns the batch held |
| Line `POST /batches/bundle`, advance and inspect items | `ifMatch` refuses a second write. A 412 item after a retried POST counts as done only if the batch left the state the step started from |
| Line `POST /auth/machine` | Writes nothing |
| `POST /events` | The Worker skips an event it already handled |
| `POST /nightly/run` | A flagged batch is not flagged again, each write has an ETag. A repeat after a lost reply reports fewer flagged |
| `POST /reports`, `POST /hops`, `POST /changes` | Live drops a hop it holds, keeps the newest version of a row, and adds a post's log entries once by its id |

- The Worker readiness probe takes one attempt: the Dispatcher repeats it on its own.
- Reports never hold a request up. At most 1000 wait per process, the rest drop, and one post at a time carries them. See `docs/architecture/events.md` :: Trace hops.
- Cosmos: 10 attempts on 429 with up to 81 s of waits, from the SDK, which does not repeat a failed write. Service Bus: the SDK, 10 attempts, same delays.

## Stopping
Constants in `ShutdownRules`: 5 s drain, 20 s to finish. Together they fit the 30 s grace set in the chart, and `stop_grace_period` for the Line. Budgets keep one `api`, `worker` and `dispatcher` pod through a voluntary drain. A rollout surges one pod and takes none down first, except `dispatcher-node2`, which recreates. `api` keeps two pods. One pod each: `live`, whose hub is in memory, `web`, because a second pod at its limit passes the Node1 CPU cap in `deploy/caps.json`, and the node-pinned `worker-node2` and `dispatcher-node2`. Web holds no drain of its own, so a 5 s `preStop` lets the ingress move first.
1. Readiness goes 503, liveness stays 200. API `/api/ready`, Live and Line `/ready`.
2. The listener keeps serving for the drain, so the ingress can move traffic first.
3. Sockets close 1001, a new upgrade gets 503 with Retry-After.
4. The listener closes, requests in flight finish, the process exits. Work past the limit is cut.

- Line: no new walk, walks under way get 15 s, the rest are cancelled with no log entry.
- Dispatcher: no drain, it takes no traffic. The leader clears the lease holder, so the next pod need not wait out the 10 s.
- Worker readiness is a TCP check, so it stays ready until the listener closes.

## Internal
Worker routes need header `X-Internal-Token`, from a Kubernetes Secret: `POST /events`, `POST /nightly/run`, `GET /ready` (Cosmos reachable). `/healthz` is anonymous.

## Settings
Environment variables only. `ServiceSettings` lists them. A service stops at startup, before any port opens, when a required one is missing, blank or malformed. The error lists every problem by name and never shows a value.
| Service | Required | Optional |
|---|---|---|
| API | `JWT_PRIVATE_KEY`, `JWT_PUBLIC_KEY`, `ALLOWED_ORIGINS`, `LINE_MACHINE_TOKEN`, `MONITOR_MACHINE_TOKEN`, `INTERNAL_TOKEN`, `COSMOS_CONNECTION`, `SERVICEBUS_CONNECTION`, `WORKER_URL` | `LIVE_URL`, `COSMOS_DATABASE`, `COSMOS_CONTAINER`, `WORKER_HOST`, `COOKIE_SECURE`, `DEV_LOGIN_ENABLED` |
| Worker | `INTERNAL_TOKEN`, `COSMOS_CONNECTION` | `LIVE_URL`, `COSMOS_DATABASE`, `COSMOS_CONTAINER`, `Dose2WindowDays` |
| Dispatcher | `INTERNAL_TOKEN`, `COSMOS_CONNECTION`, `SERVICEBUS_CONNECTION`, `WORKER_URL` | `LIVE_URL`, `COSMOS_DATABASE`, `COSMOS_CONTAINER`, `SERVICEBUS_QUEUE`, `WORKER_HOST`, `POD_NAME`, `LEASE_NAMESPACE` |
| Live | `JWT_PUBLIC_KEY`, `ALLOWED_ORIGINS`, `INTERNAL_TOKEN`, `COSMOS_CONNECTION` | `LIVE_URL`, `COSMOS_DATABASE`, `COSMOS_CONTAINER`, `COOKIE_SECURE` |
| Line | `JWT_PUBLIC_KEY`, `ALLOWED_ORIGINS`, `LINE_MACHINE_TOKEN`, `API_URL` | `COOKIE_SECURE` |
