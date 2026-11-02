using BatchTracker.Application;
using BatchTracker.Domain;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BatchTracker.Infrastructure;

/// <summary>Live's scan, the sweep's mark, and the indexes every read of this store runs on, so no read crosses the collection for a field.</summary>
public sealed partial class MongoBatchStore
{
    private const string FirstFlag = "flags.0";

    /// <summary>Named in the read: the planner settles for the status index, which walks every settled row to find the few flagged.</summary>
    private const string FlaggedNewest = "createdAt_-1";

    /// <summary>Two indexes on one key must differ by name.</summary>
    private const string NewestFirst = "createdAt-all";

    private static readonly string[] Finished = [nameof(BatchState.Closed), nameof(BatchState.Rejected)];

    private static ProjectionDefinition<BsonDocument> NoSteps => Builders<BsonDocument>.Projection.Exclude("steps");

    /// <summary>The order open rows fill the cap in: a blocked row needs a person, an old pending row is stuck, an old flag is history.</summary>
    private static (FilterDefinition<BsonDocument> Match, SortDefinition<BsonDocument> Order, string? Index)[] OpenRows() =>
    [
        (Where.Eq(Status, PendingStatus.Dead), Sort.Ascending(CreatedAt), null),
        (Where.In(Status, [PendingStatus.Pending, PendingStatus.Sent]), Sort.Ascending(CreatedAt), null),
        (Where.Exists(FirstFlag) & Where.Exists(Status, false) & Where.Nin(State, Finished), Sort.Descending(CreatedAt), FlaggedNewest)
    ];

    private static IEnumerable<CreateIndexModel<BsonDocument>> Indexes()
    {
        var keys = Builders<BsonDocument>.IndexKeys;
        return
        [
            new(keys.Ascending(Status).Ascending(WrittenAt)),
            new(keys.Ascending(Status).Ascending(SentAt)),
            new(keys.Ascending(Status).Ascending(CreatedAt)),
            new(keys.Ascending(FarmId).Descending(CreatedAt)),
            new(keys.Ascending(FarmId).Ascending(State)),
            new(keys.Ascending(State).Descending(CreatedAt)),
            new(keys.Descending(CreatedAt), new CreateIndexOptions<BsonDocument> { Name = FlaggedNewest, PartialFilterExpression = Where.Exists(FirstFlag) }),
            new(keys.Descending(CreatedAt), new CreateIndexOptions { Name = NewestFirst })
        ];
    }

    /// <summary>
    /// One update, so a list costs one round trip. Its answer is a count and not the rows, so only a short count reads which
    /// rows carry this mark; `at` is the sweep's own, and one leader sweeps.
    /// </summary>
    public async Task<IReadOnlyList<Versioned>> MarkSentAsync(IReadOnlyList<Versioned> rows, DateTimeOffset at, CancellationToken token)
    {
        var target = await CollectionAsync(token);
        var stamp = TextOf(at);
        var atEtag = Where.Or(rows.Select(r => Where.Eq("_id", KeyOf(r.Batch.FarmId, r.Batch.Id)) & Where.Eq(Version, long.Parse(r.ETag.Trim('"')))));
        var mark = Builders<BsonDocument>.Update.Set(Status, PendingStatus.Sent).Set(SentAt, stamp)
            .Set(WrittenAt, time.GetUtcNow().ToUnixTimeMilliseconds()).Inc(Version, 1);

        var result = await target.UpdateManyAsync(atEtag, mark, cancellationToken: token);
        if (result.ModifiedCount == rows.Count) return rows;

        var keys = rows.Select(r => KeyOf(r.Batch.FarmId, r.Batch.Id));
        var mine = await target.Find(Where.In("_id", keys) & Where.Eq(SentAt, stamp)).Project(Builders<BsonDocument>.Projection.Include("_id")).ToListAsync(token);
        var won = mine.Select(d => d["_id"].AsString).ToHashSet();
        return rows.Where(r => won.Contains(KeyOf(r.Batch.FarmId, r.Batch.Id))).ToList();
    }

    public async Task<IReadOnlyList<Versioned>> GetManyAsync(IReadOnlyList<BatchKey> keys, CancellationToken token) =>
        keys.Count == 0 ? [] : await RowsAsync(Where.In("_id", keys.Select(k => KeyOf(k.FarmId, k.Id))), null, null, token);

    /// <summary>
    /// One unordered write, so a list costs one round trip and a refused row leaves the others. Its answer is a count and not
    /// the rows, so only a short count reads back which rows carry the trace this write gave them.
    /// </summary>
    public async Task<IReadOnlyList<Versioned?>> WriteManyAsync(IReadOnlyList<BundleWrite> writes, CancellationToken token)
    {
        if (writes.Count == 0) return [];
        var target = await CollectionAsync(token);
        var next = writes.Select(w => w.Batch with { Version = w.ETag is null ? 1 : w.Batch.Version + 1 }).ToList();
        var won = await WrittenAsync(target, writes.Select((w, i) => ModelOf(next[i], w.ETag)).ToList(), token);
        var mine = won == writes.Count ? null : await TracesAsync(target, next, token);
        return next.Select(b => mine is null || mine.Contains(b.Pending!.TraceId) ? new Versioned(b, ETagOf(b.Version)) : null).ToList();
    }

    private WriteModel<BsonDocument> ModelOf(Batch next, string? etag) => etag is null
        ? new InsertOneModel<BsonDocument>(ToDocument(next))
        : new ReplaceOneModel<BsonDocument>(Where.Eq("_id", KeyOf(next.FarmId, next.Id)) & Where.Eq(Version, long.Parse(etag.Trim('"'))), ToDocument(next));

    /// <summary>A duplicate is a refused row, as on a single create. Any other error is the store's, and fails the list.</summary>
    private static async Task<long> WrittenAsync(IMongoCollection<BsonDocument> target, List<WriteModel<BsonDocument>> models, CancellationToken token)
    {
        try
        {
            var result = await target.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false }, token);
            return result.InsertedCount + result.MatchedCount;
        }
        catch (MongoBulkWriteException<BsonDocument> e) when (e.WriteConcernError is null && e.WriteErrors.All(w => w.Category == ServerErrorCategory.DuplicateKey))
        {
            return e.Result.InsertedCount + e.Result.MatchedCount;
        }
    }

    /// <summary>From the steps and not `pending`: the Worker can clear that before this read, and an event id repeats on a repeated create.</summary>
    private static async Task<HashSet<string>> TracesAsync(IMongoCollection<BsonDocument> target, List<Batch> next, CancellationToken token)
    {
        var keys = next.Select(b => KeyOf(b.FarmId, b.Id));
        var held = await target.Find(Where.In("_id", keys)).Project(Builders<BsonDocument>.Projection.Include("steps.traceId")).ToListAsync(token);
        return held.SelectMany(d => d["steps"].AsBsonArray).Select(s => s["traceId"].AsString).ToHashSet();
    }

    public async Task<LiveScan> FindForLiveAsync(CancellationToken token)
    {
        var counts = await CountsAsync(token);
        var open = await OpenRowsAsync(token);
        var rows = new Dictionary<string, Versioned>();
        foreach (var row in open.Take(maxOpen)) rows[row.Batch.Id] = row;
        foreach (var farm in counts.Select(c => c.FarmId).Distinct())
        {
            var newest = await RowsAsync(Where.Eq(FarmId, farm), Sort.Descending(CreatedAt), LiveScan.RowsPerFarm, token, NoSteps);
            foreach (var row in newest) rows[row.Batch.Id] = row;
        }
        return new LiveScan(counts, rows.Values.ToList(), open.Count > maxOpen);
    }

    /// <summary>Asks for one row past the cap, so a row beyond it shows and meeting the cap exactly reads as complete.</summary>
    private async Task<List<Versioned>> OpenRowsAsync(CancellationToken token)
    {
        var open = new List<Versioned>();
        foreach (var (match, order, index) in OpenRows())
        {
            var left = maxOpen + 1 - open.Count;
            if (left <= 0) break;
            open.AddRange(await RowsAsync(match, order, left, token, NoSteps, index));
        }
        return open;
    }

    /// <summary>Batches per farm and state. A $group with no $match never picks an index by itself, so this one is named: it holds both fields, so no document is read.</summary>
    private async Task<IReadOnlyList<StateCount>> CountsAsync(CancellationToken token)
    {
        var target = await CollectionAsync(token);
        var group = new BsonDocument
        {
            { "_id", new BsonDocument { { FarmId, "$" + FarmId }, { State, "$" + State } } },
            { "n", new BsonDocument("$sum", 1) }
        };
        var both = new AggregateOptions { Hint = new BsonDocument { { FarmId, 1 }, { State, 1 } } };
        var rows = await target.Aggregate(both).Group(group).ToListAsync(token);
        return rows.Select(r => new StateCount(r["_id"][FarmId].AsString, r["_id"][State].AsString, r["n"].AsInt32)).ToList();
    }
}
