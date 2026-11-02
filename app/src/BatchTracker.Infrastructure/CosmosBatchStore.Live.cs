using System.Net;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using Microsoft.Azure.Cosmos;

namespace BatchTracker.Infrastructure;

/// <summary>What Live scans and the sweep's mark, apart from the writes the rest of the store handles.</summary>
public sealed partial class CosmosBatchStore
{
    private const string LiveColumns = "c.id, c.farmId, c.product, c.version, c.state, c.createdAt, c.reason, c.failNext, c.flags, c.pending, c._etag";

    public async Task<IReadOnlyList<Versioned>> GetManyAsync(IReadOnlyList<BatchKey> keys, CancellationToken token) =>
        (await Task.WhenAll(keys.Select(k => GetAsync(k.FarmId, k.Id, token)))).OfType<Versioned>().ToList();

    /// <summary>A transactional batch is all or nothing inside one partition, so one refused row would fail the rest. Each write is its own call.</summary>
    public async Task<IReadOnlyList<Versioned?>> WriteManyAsync(IReadOnlyList<BundleWrite> writes, CancellationToken token) =>
        await Task.WhenAll(writes.Select(w => w.ETag is null ? CreateAsync(w.Batch, token) : ReplaceAsync(w.Batch, w.ETag, token)));

    /// <summary>Cosmos has no update across partitions, so each row is its own patch at its own ETag.</summary>
    public async Task<IReadOnlyList<Versioned>> MarkSentAsync(IReadOnlyList<Versioned> rows, DateTimeOffset at, CancellationToken token)
    {
        var target = await ContainerAsync(token);
        var won = await Task.WhenAll(rows.Select(row => MarkAsync(target, row, at, token)));
        return rows.Where((_, i) => won[i]).ToList();
    }

    private static async Task<bool> MarkAsync(Container target, Versioned row, DateTimeOffset at, CancellationToken token)
    {
        PatchOperation[] mark =
        [
            PatchOperation.Set("/pending/status", PendingStatus.Sent),
            PatchOperation.Set("/pending/sentAt", at),
            PatchOperation.Increment("/version", 1)
        ];
        try
        {
            var only = new PatchItemRequestOptions { IfMatchEtag = row.ETag, EnableContentResponseOnWrite = false };
            await target.PatchItemAsync<Batch>(row.Batch.Id, new PartitionKey(row.Batch.FarmId), mark, only, token);
            return true;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    /// <summary>The order open rows fill the cap in: a blocked row needs a person, an old pending row is stuck, an old flag is history.</summary>
    private static readonly (string Where, string Order)[] OpenRows =
    [
        ($"c.pending.status = '{PendingStatus.Dead}'", "ASC"),
        ($"c.pending.status != '{PendingStatus.Dead}'", "ASC"),
        ($"ARRAY_LENGTH(c.flags) > 0 AND NOT IS_DEFINED(c.pending.status) AND c.state NOT IN ('{BatchState.Closed}', '{BatchState.Rejected}')", "DESC")
    ];

    public async Task<LiveScan> FindForLiveAsync(CancellationToken token)
    {
        var counts = await CountsAsync(token);
        var open = await OpenRowsAsync(token);
        var rows = new Dictionary<string, Versioned>();
        foreach (var row in open.Take(maxOpen)) rows[row.Batch.Id] = row;
        foreach (var farm in counts.Select(c => c.FarmId).Distinct())
        {
            var newest = new QueryDefinition($"SELECT TOP {LiveScan.RowsPerFarm} {LiveColumns} FROM c ORDER BY c.createdAt DESC");
            foreach (var row in await QueryAsync(newest, token, farm)) rows[row.Batch.Id] = row;
        }
        return new LiveScan(counts, rows.Values.ToList(), open.Count > maxOpen);
    }

    /// <summary>Asks for one row past the cap, so a row beyond it shows and meeting the cap exactly reads as complete.</summary>
    private async Task<List<Versioned>> OpenRowsAsync(CancellationToken token)
    {
        var open = new List<Versioned>();
        foreach (var (where, order) in OpenRows)
        {
            var left = maxOpen + 1 - open.Count;
            if (left <= 0) break;
            open.AddRange(await QueryAsync(new QueryDefinition($"SELECT TOP {left} {LiveColumns} FROM c WHERE {where} ORDER BY c.createdAt {order}"), token));
        }
        return open;
    }

    public async Task<DateTimeOffset> ClockAsync(CancellationToken token) =>
        DateTimeOffset.FromUnixTimeMilliseconds((await OneNumberAsync("SELECT VALUE GetCurrentTimestamp()", token)).Sum());

    public async Task<long> CountInProgressAsync(CancellationToken token) =>
        (await OneNumberAsync($"SELECT VALUE COUNT(1) FROM c WHERE c.pending.status IN ('{PendingStatus.Pending}', '{PendingStatus.Sent}')", token)).Sum();

    private async Task<List<long>> OneNumberAsync(string query, CancellationToken token)
    {
        var target = await ContainerAsync(token);
        var numbers = new List<long>();
        using var iterator = target.GetItemQueryIterator<long>(new QueryDefinition(query));
        while (iterator.HasMoreResults) numbers.AddRange(await iterator.ReadNextAsync(token));
        return numbers;
    }

    /// <summary>Batches per farm and state, across every row.</summary>
    private async Task<IReadOnlyList<StateCount>> CountsAsync(CancellationToken token)
    {
        var target = await ContainerAsync(token);
        var counts = new List<StateCount>();
        using var iterator = target.GetItemQueryIterator<JsonElement>(new QueryDefinition(
            "SELECT c.farmId, c.state, COUNT(1) AS n FROM c GROUP BY c.farmId, c.state"));
        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(token))
                counts.Add(new StateCount(item.GetProperty("farmId").GetString()!, item.GetProperty("state").GetString()!, item.GetProperty("n").GetInt32()));
        }
        return counts;
    }
}
