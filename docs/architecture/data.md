# Data


## Data
Cosmos database `batch-tracker`, container `batches`, partition key `/farmId`. One document per batch, so one write is atomic.
```json
{
  "id": "guid", "farmId": "farm-a", "product": "text", "version": 1,
  "state": "Created|Dose1|Dose2|Inspected|Shipped|Closed|Rejected",
  "createdAt": "", "reason": null, "failNext": false,
  "flags": [{ "type": "Dose2Overdue", "at": "" }],
  "steps": [{ "state": "", "at": "", "by": "", "traceId": "", "recordId": "", "hash": "", "previousHash": "" }],
  "pending": { "eventId": "batchId:state", "traceId": "", "status": "pending|sent|dead", "sentAt": null, "error": null }
}
```
- `steps[n]` record fields are empty until the Worker fills them.
- `version` starts at 1 and rises by 1 on every replace, so readers of pushed rows can order them. A document written before this field reads as 0.
- Partition key: most reads are per farm, farm ids spread writes. All-farm admin list crosses partitions on purpose.

## State machine
`Created -> Dose1 -> Dose2 -> Inspected -> Shipped -> Closed`. At Dose2, inspect fail goes to `Rejected`. Closed and Rejected are final.
A batch is Blocked when `pending.status` is `dead`. Advance and inspect are refused (409) while `pending` exists, so one event per batch is in flight and order holds without sessions.
Dose 2 window: `Dose2WindowDays` config, default 21, placeholder until the real rules are known.
