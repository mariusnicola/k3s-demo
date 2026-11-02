using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace BatchTracker.Infrastructure;

/// <summary>
/// Holds each batch as the JSON the API shows, plus `_id` and `writtenAt`. Dates stay strings, because a Mongo date holds
/// milliseconds and HashChain hashes to the tick; all are written UTC, so they sort as time.
/// </summary>
public sealed partial class MongoBatchStore(IMongoClient client, string databaseName, string collectionName, TimeProvider time, int maxOpen = LiveScan.MaxOpen)
    : IBatchStore, IStoreReadings, IDisposable
{
    /// <summary>Stated here and not left to the driver or a connection string: the Dispatcher's limits rest on it.</summary>
    private const int MaxConnections = 100;
    private const string Status = "pending.status";
    private const string SentAt = "pending.sentAt";
    private const string WrittenAt = "writtenAt";
    private const string CreatedAt = "createdAt";
    private const string FarmId = "farmId";
    private const string State = "state";
    private const string Version = "version";

    /// <summary>A backstop only: each caller sets the limit its own answer needs.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonWriterSettings Plain = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };
    private static FilterDefinitionBuilder<BsonDocument> Where => Builders<BsonDocument>.Filter;
    private static SortDefinitionBuilder<BsonDocument> Sort => Builders<BsonDocument>.Sort;

    private readonly SemaphoreSlim gate = new(1, 1);
    private IMongoCollection<BsonDocument>? collection;

    /// <summary>A lost match must stay a lost match, so the driver never repeats a write, and an unreachable server fails fast.</summary>
    public static IMongoClient CreateClient(string connection)
    {
        var settings = MongoClientSettings.FromConnectionString(connection);
        settings.RetryWrites = false;
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        settings.MaxConnectionPoolSize = MaxConnections;
        return new MongoClient(settings);
    }

    public async Task<Versioned?> CreateAsync(Batch batch, CancellationToken token)
    {
        var target = await CollectionAsync(token);
        var first = batch with { Version = 1 };
        try
        {
            await target.InsertOneAsync(ToDocument(first), cancellationToken: token);
            return new Versioned(first, ETagOf(first.Version));
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return null;
        }
    }

    public async Task<Versioned?> GetAsync(string farmId, string id, CancellationToken token)
    {
        var target = await CollectionAsync(token);
        var found = await target.Find(Where.Eq("_id", KeyOf(farmId, id))).FirstOrDefaultAsync(token);
        return found is null ? null : ToRow(found);
    }

    public async Task<Versioned?> ReplaceAsync(Batch batch, string etag, CancellationToken token)
    {
        if (!long.TryParse(etag.Trim('"'), out var held)) return null;
        var target = await CollectionAsync(token);
        var next = batch with { Version = batch.Version + 1 };
        var match = Where.Eq("_id", KeyOf(batch.FarmId, batch.Id)) & Where.Eq(Version, held);
        var result = await target.ReplaceOneAsync(match, ToDocument(next), cancellationToken: token);
        return result.MatchedCount == 0 ? null : new Versioned(next, ETagOf(next.Version));
    }

    public async Task<IReadOnlyList<Versioned>> ListAsync(BatchFilter filter, CancellationToken token)
    {
        var match = filter.Farms is null ? Where.Empty : Where.In(FarmId, filter.Farms);
        if (filter.State is { } state) match &= Where.Eq(State, state.ToString());
        return await RowsAsync(match, Sort.Descending(CreatedAt), filter.Limit, token);
    }

    public Task<IReadOnlyList<Versioned>> FindByPendingStatusAsync(string status, int max, CancellationToken token) =>
        RowsAsync(Where.Eq(Status, status), Sort.Ascending(WrittenAt), max, token, NoSteps);

    public Task<IReadOnlyList<Versioned>> FindSentBeforeAsync(DateTimeOffset cutoff, int max, CancellationToken token) =>
        RowsAsync(Where.Eq(Status, PendingStatus.Sent) & Where.Lt(SentAt, TextOf(cutoff)), Sort.Ascending(SentAt), max, token, NoSteps);

    public Task<IReadOnlyList<Versioned>> FindInStateAsync(BatchState state, CancellationToken token) =>
        RowsAsync(Where.Eq(State, state.ToString()), null, null, token);

    public async Task<IReadOnlyList<Batch>> FindForReportsAsync(IReadOnlyList<string>? farms, CancellationToken token)
    {
        var rows = await RowsAsync(farms is null ? Where.Empty : Where.In(FarmId, farms), null, null, token);
        return rows.Select(r => r.Batch).ToList();
    }

    public async Task<bool> ReachableAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await client.GetDatabase(databaseName).RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: timeout.Token);
            return true;
        }
        catch (Exception e) when (e is MongoException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<DateTimeOffset> ClockAsync(CancellationToken token)
    {
        var reply = await client.GetDatabase(databaseName).RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: token);
        return new DateTimeOffset(reply["localTime"].ToUniversalTime(), TimeSpan.Zero);
    }

    public async Task<long> CountInProgressAsync(CancellationToken token)
    {
        var target = await CollectionAsync(token);
        return await target.CountDocumentsAsync(Where.In(Status, [PendingStatus.Pending, PendingStatus.Sent]), cancellationToken: token);
    }

    public void Dispose() => gate.Dispose();

    private async Task<IReadOnlyList<Versioned>> RowsAsync(
        FilterDefinition<BsonDocument> match, SortDefinition<BsonDocument>? order, int? limit, CancellationToken token,
        ProjectionDefinition<BsonDocument>? columns = null, string? index = null)
    {
        var target = await CollectionAsync(token);
        var query = target.Find(match, new FindOptions { Hint = index });
        if (columns is not null) query = query.Project<BsonDocument>(columns);
        if (order is not null) query = query.Sort(order);
        if (limit is not null) query = query.Limit(limit);
        return (await query.ToListAsync(token)).Select(ToRow).ToList();
    }

    /// <summary>`_id` is both parts because one id can exist in two farms; a plain id would make that a duplicate key.</summary>
    private static string KeyOf(string farmId, string id) => $"{farmId}/{id}";

    private static string ETagOf(long version) => $"\"{version}\"";

    private static string TextOf(DateTimeOffset at) => JsonSerializer.Serialize(at, JsonDefaults.Options).Trim('"');

    private BsonDocument ToDocument(Batch batch)
    {
        var document = BsonDocument.Parse(JsonSerializer.Serialize(batch, JsonDefaults.Options));
        document["_id"] = KeyOf(batch.FarmId, batch.Id);
        document[WrittenAt] = time.GetUtcNow().ToUnixTimeMilliseconds();
        return document;
    }

    private static Versioned ToRow(BsonDocument document)
    {
        document.Remove("_id");
        document.Remove(WrittenAt);
        var batch = JsonSerializer.Deserialize<Batch>(document.ToJson(Plain), JsonDefaults.Options)!;
        return new Versioned(batch, ETagOf(batch.Version));
    }

    /// <summary>Created on first use, so a service starts and reports not ready while Mongo is away.</summary>
    private async ValueTask<IMongoCollection<BsonDocument>> CollectionAsync(CancellationToken token)
    {
        if (collection is not null) return collection;
        await gate.WaitAsync(token);
        try
        {
            if (collection is not null) return collection;
            var target = client.GetDatabase(databaseName).GetCollection<BsonDocument>(collectionName)
                .WithWriteConcern(new WriteConcern(1, journal: true));
            await target.Indexes.CreateManyAsync(Indexes(), token);
            return collection = target;
        }
        finally
        {
            gate.Release();
        }
    }
}
