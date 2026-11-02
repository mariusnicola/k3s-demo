# Store and queued writes

Store built, on Mongo from empty: Cosmos data stays where it is, untouched. Queued writes are design, not built. `data.md`, `api.md` and `events.md` hold what runs today and change when each part ships. Decision: `docs/decisions/0004-mongodb-and-queued-writes.md`.

## Store seam
`IBatchStore` keeps its methods. `AddBatchStore` picks the class from `BATCH_STORE`, `mongo` or `cosmos`, required, no default. Only the chosen store's settings are required.

| Setting | Value |
|---|---|
| `MONGO_CONNECTION` | Secret. `serverSelectionTimeoutMS=3000`, `retryWrites=false` |
| `MONGO_DATABASE` | `batch-tracker`. Tests: `batch-tracker-test` |
| `MONGO_COLLECTION` | `batches` |

What Cosmos gives for free and `MongoBatchStore` must supply:

| Today | On Mongo |
|---|---|
| `_etag` | ETag is the quoted version, `"12"`. Replace filters on `_id` and that version. A lost match returns null, as today |
| Partition `farmId`: one id can exist in two farms | `_id` is `farmId/id`, and `id` stays a field. A plain `_id` would turn a repeated key on another farm into 409 |
| `_ts`, seconds, server clock | `writtenAt`, milliseconds, set by the store from `TimeProvider`. It orders the sweep and is fairness only, so pod clock skew costs no safety |
| One read per farm in the sweep | One query on an index |
| SDK retries 429 | Mongo has no 429. Any other failure reaches the caller as it does now |
| Container created on first use | Collection and indexes created on first use, once, so a service starts and reports not ready while Mongo is away |

- Document: `JsonSerializer` with `JsonDefaults.Options`, then BSON. Same shape as `data.md` plus `_id` and `writtenAt`, both stripped on read.
- Dates stay strings. All are written through `GetUtcNow`, so they end `+00:00` and sort as time as strings. A contract test holds this across fraction lengths.
- Write concern `w:1, j:true`: an acknowledged write survives a crash.
- Indexes: `{pending.status, writtenAt}`, `{pending.status, pending.sentAt}`, `{farmId, createdAt: -1}`, `{state}`.
- `FindForLiveAsync`: a `$group` for counts, projection without `steps` for rows, same caps.
- `HashChain` does not change.

Words that name Cosmos become `db`, one commit each with its lane: web stations, the `cosmos` field of `/api/ready/deep`, the monitor and smoke check. Hop `cosmos` is gone: the one `api` hop says accepted and saved.

## Running locally
Compose `deploy/mongo/docker-compose.yaml`, container `batchtracker-mongo`, deploy lane.
- Image `mongo` at an exact 8.0 patch tag, never `latest`. Named volume.
- Cap from `deploy/caps.json`, `CAP_MONGO`: 1000 milliCPU, 1024 MiB, `--wiredTigerCacheSizeGB 0.25`. Starting numbers, unmeasured.
- Port 27018, bound on `127.0.0.1` and `<HOST_IP>` like every compose in `deploy/`, so pods on `k3s-node1` and `k3s-node2` reach it. 27017 already has a listener on this PC that is not ours.
- A password, in the gitignored `.env`.
- The test database is `batch-tracker-test`.

## Queued writes
Applies to API create, advance, inspect, fail-next and replay.

```
API: permission, validation, read, If-Match, machine  -> refuse now as today
     send BatchWrite to batch-writes                  -> 503 if the send fails
     202 { writeId, status: "accepted" }, Location /api/writes/{writeId}
Dispatcher leader: receive, POST Worker /writes       -> as /events
Worker: re-check on the current row, write, record the result
```

- `BatchWrite`: `writeId` (UUIDv7, so it carries its time), kind, farm, batch, `ifMatch`, the intent (target state, passed, reason, product), caller, correlation id, trace id.
- Queue `batch-writes`, tests `batch-writes-test`. Service Bus session id is the batch id, so writes to one batch apply in order.
- Create keeps its id from `Idempotency-Key`, so a repeat lands on the same document.
- Counters and the `db` hop move to the Worker, after the write. The `api` hop stays at accept.

### Re-check
A 202 means accepted, never applied. Three checks close the gap.
1. **On apply.** The Worker reads the current row, compares `ifMatch`, and runs `BatchMachine` again. Another writer may have moved the batch since the API looked. A loss records `refused`, with the status the API would have given: 412 or 409.
2. **On redelivery.** The document holds `lastWriteId`. A row already holding this id is done, so a crash between the write and its record neither applies twice nor reads as stale.
3. **After apply.** Collection `writes`, one row per result, TTL 24 h: `{ writeId, farmId, batchId, status, etag, failure, at }`. Status is `applied` or `refused`. A dead letter sets the batch's pending status to `dead` (`DeadLetters.cs:21`), and writes no `writes` row.

`GET /api/writes/{writeId}`, same permission as reading the batch:

| Answer | Means |
|---|---|
| `accepted` | No result yet |
| `applied`, with the ETag | Written |
| `refused`, with an RFC 7807 problem | Not written, and why |
| `unconfirmed` | No result 60 s after the time in the id. The message may be lost, so a person reads the batch. Logged as a warning |

- Web: a `write` socket frame carries the same fields to the farm's clients, so the page never polls. It reads the route once after a reconnect.
- Line: reads the route between steps.
- `lastWriteId` is not hashed. The hash covers named fields only.

## Dispatcher budget on Mongo
- Unchanged: 200 ms, 10,000 documents, 16 writers, 40 samples after 5 warm-up, pending and stale queries.
- Same test through `IBatchStore`, on the capped container. Budget and asserts stay.
- Not measured. No Mongo has run here. The first number is reported as read, with the container's cap, on this PC.
- It measures the query, not production: one node, one PC.

## Test migration
Failing test first, in this order. Single classes.

1. `StoreContract`, abstract, one test per promise of `IBatchStore`, including the table above: `MongoStoreTests` red, `CosmosStoreTests` green on today's store.
2. `MongoBatchStore` until the contract passes.
3. `Stack` takes `BATCH_STORE=mongo`, `MONGO_CONNECTION=mongodb://127.0.0.1:27018`, database `batch-tracker-test`. `Stack.Cosmos` gives way to `Stack.Documents`, a raw read, for the classes that touch Cosmos directly: `BatchRouteTests`, `BatchWriteTests`, `QueryBudgetTests`, `LiveScanStoreTests`, `StartupTests`.
4. Classes one at a time on Mongo, then the budget. Reds reported, no assert changed.
5. Arch: `DependencyRuleTests`, the attempt limit rule, `ServiceSettings`.
6. Queue: `WriteApplierTests` for the re-check, red first, then route tests. An assert on 201, 200 or an ETag on a change route changes only where the new contract requires it, listed row by row first.

`CosmosStoreTests` stays, so the Cosmos path stays proved. Deploy, monitor and smoke are the deploy lane's. The web is the design lane's.

## Open
- **Image pull.** Needs the owner's yes before the deploy lane starts the container.
- **Queue config.** `batch-writes` and `batch-writes-test` go in `deploy/servicebus/config.json`. Applying that to the running emulator is the deploy lane's, and needs a restart if it reads the file only at start, not checked.
- **Cosmos emulator after the switch.** Stays up until the owner says otherwise. `CosmosStoreTests` needs it.
