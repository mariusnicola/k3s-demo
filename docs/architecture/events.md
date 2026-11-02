# Event flow

## Flow
1. API checks permission, farm, state and If-Match, then replaces the document with new state, new step, `pending` with status `pending` and `storedAt` on the store's clock, see `docs/architecture/monitoring.md` :: Metric names. A bundle does this per item, with one read and one unordered write on Mongo and one call per row on Cosmos, where a transactional batch would fail every item with one.
2. Dispatcher active: query loop every 200 ms, doubling to at most 4 s while a sweep moves no row, back to 200 ms when one moves (4 s is unmeasured). `pending.status='pending'`, TOP 200 oldest first by last write, one read per farm, merged. Stale sent events also go oldest first. Each read leaves the steps behind. Sets `sent` and `sentAt` at each row's If-Match first, as one update on Mongo and one patch per row on Cosmos, then sends the marked rows to queue `batch-events` as one message, a pack: `Subject` `pack`, body a JSON array of events, at most 400 events and 113 KB against a 256 KB limit, so nothing splits one. A message with no `Subject` is an old single event, read as a pack of one. A row that loses its If-Match is skipped. The mark reports no change to Live: the Worker's write reports the whole row next. A full list waits for the next round like any other, so the bus gets at most one pack per 200 ms. Order holds per batch, because `pending` blocks the next write. Nothing promises an order between batches. A failed send fails its whole pack, and the loop retries after 1 s. So a bus outage marks up to 400 rows a round, a list of due rows and one of stale, each healed by the stale sweep, which takes 200 per 30 s after 120 s.
3. Same loop, at most once per 30 s (unmeasured): `sent` with `sentAt` older than 120 s gets a new `sentAt` and is re-sent. Covers a crash after marking and an emulator restart, which loses messages.
4. Dispatcher receives one message at a time and gives each of its events one of 40 slots, 4 for each of 10 Workers, each delivery freeing its own, so a slow one holds back no other. The next message waits until every event of this one has had a slot. Dead letters are read 1 at a time, on their own limit, because a letter can be a pack. Each store client keeps a pool of 100 connections. An event taken off the queue reports hop `deliver`, once per delivery, so a redelivery counts again. POST to Worker via ingress, timeout 10 s, lock 30 s, MaxDeliveryCount 10. Each recorded outcome counts down, and the last one completes the message.
   - 200 counts down. 422: `pending.status='dead'`, then counts down.
   - 409, `Batch kept changing`: log, row back to `pending`, count down. The Worker is there.
   - Anything else: log, row back to `pending`, count down. After 3 of these in a row, with no Worker answer between them, stop receiving and sweeping, GET Worker `/ready` with the internal token every 5 s, resume when it answers 200. An event in hand as this starts goes back the same way, unposted.
   - `pending.eventId` differs: nothing to do, count down.
   - Row write fails or loses its If-Match: no count down. The lock lapses, the whole message comes back within 30 s, and done events answer 200 by event id.
5. Worker: read document. `pending` absent or `pending.eventId` differs: 200, nothing to do. `failNext`: 422. Otherwise fill step record, clear `pending`, replace with If-Match, on 412 re-read once.
6. Dispatcher reads the dead-letter sub-queue, where a message lands after 10 lapsed locks or at 1 h expiry. Each event goes back: `pending.status='sent'` with a matching `pending.eventId` becomes `pending`, anything else stays, so `dead` stays `dead`. The last event completes the letter. A lost write skips its count down and the letter comes back in 30 s. A letter left by older code goes back too: the Worker refuses it again and the row goes `dead` there. An event goes `dead` only on a 422.
7. Replay (admin): `pending.status='pending'`, `failNext=false`.
8. Nightly: Dose1 batches past the window get a flag, once.

Query budget: under 200 ms at 10,000 documents, measured on the emulator.

Going back to code from before packs: run `docker restart batchtracker-servicebus` after the deploy. A pack can outlive its rows and that code throws on one. The restart empties the queues, and the rows are already done.

## Leader election
Lease `dispatcher` in namespace `batch-tracker`, 10 s duration, 5 s renew. Only the holder connects to Service Bus, loops and receives. Renew lost: close the connection at once. All 3 pods stay Ready.
A failed lease call on a standby ends the elector too, and the election starts a new one. The runner's wait survives that, so the next gain starts a term.
`GET /status` on each pod returns `{ leader, connected, secondsSinceSweep }`. `secondsSinceSweep` is null with no term, starts from zero at the start of a term, and resets after each sweep round that finishes, moved rows or not. A sweep paused by a Worker outage, or failing, does not reset it.

## Trace hops
Each pod holds what it tells Live in one buffer of 1000 reports, and every 100 ms posts all of it as one `POST http://live:8080/reports` with `X-Internal-Token`, never reachable through ingress: `{ "hops", "changes", "logs", "postId" }`. A post retries under the service rule, see `docs/architecture/api.md` :: Retries, each attempt limited to 1 s. The flush loop waits for it, so a pod has one post in flight, a caller is never held, and what arrives meanwhile waits in the buffer. A post that runs out of attempts loses what it carried. Past the cap a report is dropped and counted. Live drops a repeated hop, keeps the newest row, and adds a post's log entries once by `postId`, remembering the last 20000 ids. A stopping pod posts what it holds, one attempt, so Live being away cannot hold the stop. A post still retrying when the pod stops is lost.

A hop: `{ "traceId", "batchId", "farmId", "hop": "api|dispatcher|bus|deliver|worker|record", "state", "at", "ok", "pod", "correlationId" }`. `pod` is the pod that handled the hop, from `POD_NAME`, set by Worker and Dispatcher and absent on API hops.
Live keeps the last 500. A lost hop loses a dot, nothing else. Frames: see Sockets.

A change: `{ "etag", "batch" }`, the full row after a write, the sweep's mark apart. A lost one is healed by the Live reconcile, see Sockets.

## Line
`POST /line {rate}`, `DELETE /line`, `GET /line`, each needs ops.admin, by session cookie or bearer. Rate is new batches per second, 0 to 35, default 1; anything else is a 400 and leaves the line as it was. `GET /line/bundles` needs any sign-in and answers figures only: sent, items, failed items, failed requests, last items, last ms, last failure, failing. Creates a batch and walks it to Closed through the API with its own credential, see `docs/architecture/api.md` :: Sign in, every step due going out once a second in one `POST /api/batches/bundle`, one POST at a time, nothing on an empty tick, each walk taking its own result, taking the batch's ETag between steps from the `batch` frame of one bearer socket on `/ws/hops`, reached by the host in `API_URL`, because each read loads the store the event waits on. A step reads the batch once, then waits for a frame with no event pending and a version above that read. With the socket up, a read runs only every 5 s, for a lost frame. With it down, reopened every 5 s, reads wait 200 ms and double to 5 s. Each wait is spread at random half either side of that length, 2.5 to 7.5 s at the top, so walks that stepped together stop reading together while the average stays the same. At most 500 batches in flight (`LineOptions.MaxInFlight`): at the cap, new batches wait, so a slow store slows the rate and memory stays bounded. One in ten fails inspection. Also the load generator.

`GET /line` returns `{ running, rate, started, closed, rejected, errors, errorsByReason, inFlight, throttled }`. `throttled` counts 429 answers from the API. `errors` equals the sum of `errorsByReason`. All counters run since process start and survive the log rolling over. A restart resets them.

## Line log
Each batch writes entries to a memory buffer of the last 5000 (`LineLog.Capacity`). A restart empties it.
`GET /line/logs?level=&reason=&farm=&batch=&correlationId=&from=&to=&limit=`, ops.admin. `correlationId` is an exact match after trimming spaces.
- `level`: `info` or `error`. `reason`: a code below. `farm`: exact. `batch`: number or id. `from`, `to`: ISO 8601, inclusive, URL encoded. `limit`: 1 to 5000, default 200.
- Bad value: 400, RFC 7807. Filters combine with AND.
- Reply: `{ matched, byReason, droppedByLimit, evicted, entries }`, newest first. `matched` and `byReason` count every match before `limit`. `droppedByLimit` is `matched` minus entries returned. `evicted` is entries rolled out of the buffer since start.
- Entry: `{ at, level, reason, message, batchNumber, batchId, farm, step, waitedMs, httpStatus, exceptionType, exceptionMessage, traceId, correlationId, impact, nextAction }`. `impact` and `nextAction` hold one fixed text per reason, never exception text. `message` says what failed and where; exception type and message stay in their own fields. `step`: `Create`, `Dose1`, `Dose2`, `Inspect`, `Shipped`, `Closed`. `batchId`, `httpStatus`, `traceId` are null until known. `exceptionType`, `exceptionMessage` are null on info entries. `httpStatus` is the last status received, so it can be a 200 on a network failure.
- `waitedMs`: time in the failed step on errors, whole walk on `closed` and `rejected`, 0 on `started`.
- No token appears in an entry. Messages pass a filter that masks bearer tokens, JWTs, `AccountKey=` and `SharedAccessKey=` values.

| Reason | Level | Meaning |
|---|---|---|
| `started` | info | Line began a batch |
| `closed` | info | Batch reached Closed |
| `rejected` | info | One-in-ten inspection failure, on purpose |
| `event-timeout` | error | Previous event not finished after 60 s. The Line stopped waiting; the batch is not failed and its event may finish later. If its event is dead, the batch is blocked until replayed |
| `http-refused` | error | API answered non-2xx, `httpStatus` holds it, `message` holds the API's plain detail |
| `network-failure` | error | No HTTP answer, or a client timeout. The request may or may not have arrived |
| `login-failure` | error | Sign-in failed, whether refused or unreachable. The API's detail or the exception sits in `exceptionMessage` |
| `unknown` | error | Anything else. The exception sits in `exceptionType` and `exceptionMessage` |

A shutdown mid-walk writes no entry.

## Service log
API, Worker, Dispatcher and Live report each warning and error to Live. Line entries stay in `GET /line/logs`. The web merges both.
- API, Worker and Dispatcher send entries in the pod's reports, see Trace hops. Live skips an entry missing its text and keeps the rest of the post. Live writes its own directly. A lost entry stays in the pod log.
- The reporter's own HTTP client and retry categories do not report, so Live being away adds nothing.
- Live keeps the last 5000 service rows (`ServiceLog.Capacity`) in memory, and the last 1000 browser rows (`ServiceLog.BrowserCapacity`) in a queue of their own, so an anonymous report never pushes out a service row. The 1000 is the coordinator's number, unmeasured. A restart empties both.
- `GET /live/logs?level=&reason=&service=&farm=&batch=&correlationId=&from=&to=&limit=`, ops.admin. `level`: `warning` or `error`. `reason`: `service-warning` or `service-error`. `limit`: 1 to 5000, default 200, applied to each queue, so a report flood never fills the page. Bad value: 400, RFC 7807.
- Reply: `{ matched, droppedByLimit, evicted, browserEvicted, entries }`. `entries` holds at most `limit` service rows and `limit` browser rows, merged by `at`, newest first. `matched` counts every match, `droppedByLimit` the matches left out. `evicted` counts service rows pushed out, `browserEvicted` browser rows.
- Entry: the Line log entry fields plus `service` and `pod`. `batchId`, `farm` and `step` name the batch of the action that logged: a change at the API once it has read the batch, a sweep send, a delivery, a dead letter, a Worker event. A failed read or an error with no batch leaves them null. These fields travel as baggage on every outbound call of the action, Cosmos calls included. That is accepted: the same values are in the documents that account stores. `batchNumber`, `waitedMs`, `httpStatus` are null: no service log line carries them. Live checks `level`, `reason` and `service`, a bad one or an empty `message` or `pod` gets a 400, and masks and cuts each entry itself: message and exception text at 2000 characters, other text at 200.

### Browser reports
A browser failure reaches the service log, so one query shows it beside the rows of the request it came from.
- `POST /api/browser-reports`, JSON, anonymous, signed in or not. Same-origin only: an `Origin` outside `ALLOWED_ORIGINS` is 403.
- Body, at most 8 KiB, otherwise 413:

| Field | Type | Rule |
|---|---|---|
| `id` | string | Required. UUID the browser makes, so the page can show it before the post lands |
| `kind` | string | Required. `console-error`, `socket-failure`, `request-failure` or `render-fault` |
| `route` | string | Required. Page path, starts with `/`. Query and fragment are dropped, cut at 200 |
| `status` | number or null | HTTP status 100 to 599 of the failed request. Null when there was no answer or no request |
| `correlationId` | string or null | `X-Correlation-Id` of the failed request, same format as that header. Null for a failure with no request |
| `message` | string or null | Masked, cut at 2000 |

- 202 `{ "id": "..." }`. Missing or malformed field: 400, RFC 7807. Unknown fields are ignored.
- Limit: 2 a second, burst 20, per client address, its own bucket, so a failing page does not spend the sign-in budget. Over it: 429 with `Retry-After`. A 429, 400 or 413 spends no failure.
- The browser sends once and never retries, and removes repeats of the same failure on its own. A lost report is lost.
- Caller text never goes into baggage or a header. The report's `correlationId` is a stored field; the post keeps its own id as any request does.
- Stored in the service log: `level` `error`, `reason` `browser-failure`, `service` `browser`, `pod` the API pod that took it, `at` server time, `message`, `correlationId`, `httpStatus` and new fields `reportId`, `kind`, `route`, null on every other entry.
- `GET /live/logs?reportId=` returns the report and every entry sharing its `correlationId`, newest first. Other filters still apply. `GET /line/logs?correlationId=` gives the Line rows the same way.

## Sockets
Reads repeated on hops move here. `GET` routes stay for first load of a page outside the snapshot and for scripts. No browser polling.

### Rules for every socket
- Text frames, one JSON object, field `type` names it.
- Every connect starts with one `snapshot`. It replaces all client state, so a reopened socket needs no GET.
- Idle sends nothing. The server sends a protocol ping every 30 s, which pages never see, so a silent socket is not a lost one.
- Auth at upgrade: session cookie and Origin in `ALLOWED_ORIGINS`. A bearer header also opens a socket, for scripts. A token in the URL never does.
- Upgrade refused: 401 no valid session, 403 wrong Origin, a cookie with no Origin, or missing permission, 429 with Retry-After over 5 sockets per user, per pod, 503 with Retry-After while the service stops.
- Close 4401 reason `unauthorized`: token expired. Client goes to sign-in and does not reconnect.
- Close 1013 reason `too slow`: 1 MiB of frames waits for the client, or a send took over 5 s. The first frame on an empty outbox always goes. Client reconnects and gets a snapshot.
- Close 1001 reason `service stopping`: the pod is stopping. Client reconnects with backoff.
- Any other close: client reconnects with backoff.

### `/ws/hops`, signed in, Live
A farm the user cannot read never reaches the socket, in any frame. Summary and alerts are computed for the socket's farms only.
| `type` | Fields | When |
|---|---|---|
| `snapshot` | `at`, `reconciledAt` (null until the first read), `hops` (last 500), `summary`, `alerts`, `batches` (newest 200 rows, each `{ etag, batch }`), `batchesDropped` (rows beyond 200, never below 0), `capped` (true when the scan's open rows hit the cap, so pending, blocked and flagged may read short) | On connect |
| `hops` | `hops` (array of hops, oldest first) | Each 100 ms tick that has new hops |
| `batch` | `etag`, `batch` (full row as `GET /batches/{farmId}/{id}`) | Every write to a batch |
| `summary` | `summary` (as `GET /summary`), `reconciledAt`, `capped` | At most once a second, only when changed |
| `alerts` | `alerts` (full list, as `GET /alerts`) | At most once a second, only when changed |

- Frames can arrive out of order across writers. Keep the row with the highest `batch.version`, see `docs/architecture/data.md`.
- `etag` is good for If-Match, so a page can act without a GET.
- A batch older than the snapshot's 200 rows still takes one GET when its page opens, then `batch` frames keep it current.
- Live scans the store behind a running pod and reconciles every 60 s, one scan and never one per hop. A scan reads three things: state counts per farm by `GROUP BY`, every open row (5000 at most, `LiveScan.MaxOpen`: blocked rows oldest first, other pending rows oldest first, then flagged rows newest first; with a row left out a warning in the log and `capped` true; a flag on a Closed or Rejected batch is not open), and the newest 200 rows per farm. Rows hold every field but `steps`, because alerts need product and pending. A write moves a state count only when it creates a batch or changes its state. A row held moves by the state held. A row not held moves by its steps when its write raised a new pending event; any other write moves nothing. A flag, a fail-next or a requeue on a row past the cap looks like a new event, so it can move a count wrongly until the next scan, within 60 s. `byState` can be off by a few for up to 60 s. A snapshot row the scan shows as changed takes one point read. A value lost with a POST is corrected within 60 s. The first scan never holds the start and `/ready` does not wait for it, because hops need no store and a pod out of the Service would lose them. Until a scan finishes the pod takes hops and serves empty reports with `reconciledAt` null; a failed or hung first scan is tried again with the retry backoff, each attempt limited to 2 minutes. When the first scan finishes every client gets a `batch` frame per snapshot row and one `summary` frame with `reconciledAt` set, even if the counts did not change.

### `/line/ws`, ops.admin, Line
| `type` | Fields | When |
|---|---|---|
| `snapshot` | `at`, `status` (as `GET /line`), `entries` (newest 200), `entriesDropped`, `evicted` | On connect |
| `line` | `at`, `status`, `entries` (new since the last frame, newest first, at most 500), `entriesDropped`, `evicted` | At most once a second, only when anything changed |

`entries` use the Line log entry shape. `entriesDropped` counts entries left out of this frame, which `GET /line/logs` still holds.