using System.Net;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using Microsoft.Azure.Cosmos;

namespace BatchTracker.Infrastructure;

public sealed partial class CosmosBatchStore(CosmosClient client, string databaseName, string containerName, int maxOpen = LiveScan.MaxOpen) : IBatchStore, IStoreReadings, IDisposable
{
    private const string Columns = "SELECT * FROM c";
    /// <summary>A backstop only: each caller sets the limit its own answer needs.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim gate = new(1, 1);
    private Container? container;

    public static CosmosClient CreateClient(string connectionString) => new(connectionString, new CosmosClientOptions
    {
        ConnectionMode = ConnectionMode.Gateway,
        LimitToEndpoint = true,
        MaxRetryAttemptsOnRateLimitedRequests = RetryRules.MaxAttempts - 1,
        MaxRetryWaitTimeOnRateLimitedRequests = RetryRules.LongestWait,
        UseSystemTextJsonSerializerWithOptions = JsonDefaults.Options
    });

    public async Task<Versioned?> CreateAsync(Batch batch, CancellationToken token)
    {
        var target = await ContainerAsync(token);
        var first = batch with { Version = 1 };
        try
        {
            var response = await target.CreateItemAsync(first, new PartitionKey(batch.FarmId), cancellationToken: token);
            return new Versioned(first, response.ETag);
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return null;
        }
    }

    public async Task<Versioned?> GetAsync(string farmId, string id, CancellationToken token)
    {
        var target = await ContainerAsync(token);
        try
        {
            var response = await target.ReadItemAsync<Batch>(id, new PartitionKey(farmId), cancellationToken: token);
            return new Versioned(response.Resource, response.ETag);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Versioned?> ReplaceAsync(Batch batch, string etag, CancellationToken token)
    {
        var target = await ContainerAsync(token);
        var next = batch with { Version = batch.Version + 1 };
        try
        {
            var response = await target.ReplaceItemAsync(
                next, batch.Id, new PartitionKey(batch.FarmId),
                new ItemRequestOptions { IfMatchEtag = etag }, token);
            return new Versioned(next, response.ETag);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<Versioned>> ListAsync(BatchFilter filter, CancellationToken token)
    {
        var conditions = new List<string>();
        if (filter.Farms is not null) conditions.Add("ARRAY_CONTAINS(@farms, c.farmId)");
        if (filter.State is not null) conditions.Add("c.state = @state");
        var where = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";

        var query = new QueryDefinition($"SELECT TOP {filter.Limit} * FROM c{where} ORDER BY c.createdAt DESC");
        if (filter.Farms is not null) query.WithParameter("@farms", filter.Farms);
        if (filter.State is not null) query.WithParameter("@state", filter.State.ToString());
        return QueryAsync(query, token);
    }

    /// <summary>Oldest first, because a bounded read otherwise lets new events pass ones already waiting.</summary>
    public async Task<IReadOnlyList<Versioned>> FindByPendingStatusAsync(string status, int max, CancellationToken token)
    {
        var query = new QueryDefinition($"SELECT TOP {max} {LiveColumns}, c._ts FROM c WHERE c.pending.status = @status ORDER BY c._ts")
            .WithParameter("@status", status);
        var farms = await PerFarmAsync(query, token);
        return farms.SelectMany(rows => rows).OrderBy(r => r.WrittenAt).Take(max).Select(r => r.Row).ToList();
    }

    /// <summary>Oldest first, so the event silent longest is sent again first.</summary>
    public async Task<IReadOnlyList<Versioned>> FindSentBeforeAsync(DateTimeOffset cutoff, int max, CancellationToken token)
    {
        var query = new QueryDefinition($"SELECT TOP {max} {LiveColumns}, c._ts FROM c WHERE c.pending.status = @status AND c.pending.sentAt < @cutoff ORDER BY c.pending.sentAt")
            .WithParameter("@status", PendingStatus.Sent)
            .WithParameter("@cutoff", cutoff);
        var farms = await PerFarmAsync(query, token);
        return farms.SelectMany(rows => rows).OrderBy(r => r.Row.Batch.Pending!.SentAt).Take(max).Select(r => r.Row).ToList();
    }

    /// <summary>One read per farm at once: a read across farms costs the sum of them and these run every sweep.</summary>
    private Task<List<(Versioned Row, long WrittenAt)>[]> PerFarmAsync(QueryDefinition query, CancellationToken token) =>
        Task.WhenAll(DemoUsers.KnownFarms.Select(farm => RowsAsync(query, token, farm)));

    public Task<IReadOnlyList<Versioned>> FindInStateAsync(BatchState state, CancellationToken token) =>
        QueryAsync(new QueryDefinition($"{Columns} WHERE c.state = @state")
            .WithParameter("@state", state.ToString()), token);

    public async Task<IReadOnlyList<Batch>> FindForReportsAsync(IReadOnlyList<string>? farms, CancellationToken token)
    {
        var query = farms is null
            ? new QueryDefinition(Columns)
            : new QueryDefinition($"{Columns} WHERE ARRAY_CONTAINS(@farms, c.farmId)").WithParameter("@farms", farms);
        return (await QueryAsync(query, token)).Select(r => r.Batch).ToList();
    }

    public async Task<bool> ReachableAsync
(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await (await ContainerAsync(timeout.Token)).ReadContainerAsync(cancellationToken: timeout.Token);
            return true;
        }
        catch (Exception e) when (e is CosmosException or OperationCanceledException or HttpRequestException)
        {
            return false;
        }
    }

    public void Dispose() => gate.Dispose();

    private async Task<IReadOnlyList<Versioned>> QueryAsync(QueryDefinition query, CancellationToken token, string? farm = null) =>
        (await RowsAsync(query, token, farm)).Select(r => r.Row).ToList();

    private async Task<List<(Versioned Row, long WrittenAt)>> RowsAsync(QueryDefinition query, CancellationToken token, string? farm)
    {
        var target = await ContainerAsync(token);
        var rows = new List<(Versioned, long)>();
        var options = farm is null ? null : new QueryRequestOptions { PartitionKey = new PartitionKey(farm) };
        using var iterator = target.GetItemQueryIterator<JsonElement>(query, requestOptions: options);
        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(token))
            {
                var row = new Versioned(item.Deserialize<Batch>(JsonDefaults.Options)!, item.GetProperty("_etag").GetString()!);
                rows.Add((row, item.TryGetProperty("_ts", out var written) ? written.GetInt64() : 0));
            }
        }
        return rows;
    }

    /// <summary>Created on first use, so a service starts and reports not ready while Cosmos is away.</summary>
    private async ValueTask<Container> ContainerAsync(CancellationToken token)
    {
        if (container is not null) return container;
        await gate.WaitAsync(token);
        try
        {
            if (container is not null) return container;
            Database database = await client.CreateDatabaseIfNotExistsAsync(databaseName, cancellationToken: token);
            return container = await database.CreateContainerIfNotExistsAsync(containerName, "/farmId", cancellationToken: token);
        }
        finally
        {
            gate.Release();
        }
    }
}
