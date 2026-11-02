using System.Diagnostics;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using Microsoft.Azure.Cosmos;
using Xunit.Abstractions;

namespace BatchTracker.IntegrationTests;

/// <summary>Uses its own container, deleted at the end, so the 10,000 documents never mix with other tests.</summary>
[Collection(StackCollection.Name)]
public sealed class QueryBudgetTests(Stack stack, ITestOutputHelper output)
{
    public const int Documents = 10_000;
    public const int Waiting = 50;
    public const int Writers = 16;
    private const int Warmup = 5;
    private const int Samples = 40;
    public const double BudgetMs = 200;
    private const string ContainerName = "batches-budget";

    [Fact]
    public async Task Dispatcher_query_stays_under_200_ms_at_10000_documents()
    {
        var database = stack.Cosmos.GetDatabase(Env.Database);
        Container container = await database.CreateContainerIfNotExistsAsync(ContainerName, "/farmId");
        try
        {
            var loading = Stopwatch.StartNew();
            await LoadAsync(container);
            output.WriteLine($"loaded {Documents} documents with {Writers} writers in {loading.Elapsed.TotalSeconds:F1} s");

            using var store = new CosmosBatchStore(stack.Cosmos, Env.Database, ContainerName);
            var pending = await MeasureAsync(output, "pending TOP 50", () =>
                store.FindByPendingStatusAsync(PendingStatus.Pending, 50, default), Waiting);
            var stale = await MeasureAsync(output, "stale sent TOP 50", () =>
                store.FindSentBeforeAsync(DateTimeOffset.UtcNow.AddSeconds(-120), 50, default), Waiting);

            Assert.True(pending < BudgetMs, $"pending query p95 {pending:F1} ms");
            Assert.True(stale < BudgetMs, $"stale query p95 {stale:F1} ms");
        }
        finally
        {
            await container.DeleteContainerAsync();
        }
    }

    public static async Task<double> MeasureAsync(ITestOutputHelper output, string name, Func<Task<IReadOnlyList<Versioned>>> query, int expected)
    {
        for (var i = 0; i < Warmup; i++) await query();

        var times = new List<double>();
        for (var i = 0; i < Samples; i++)
        {
            var watch = Stopwatch.StartNew();
            var rows = await query();
            times.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Equal(expected, rows.Count);
        }
        times.Sort();
        var p50 = times[times.Count / 2];
        var p95 = times[(int)(times.Count * 0.95)];
        output.WriteLine($"{name}: p50 {p50:F1} ms, p95 {p95:F1} ms, max {times[^1]:F1} ms, {Samples} samples after {Warmup} warm-up");
        return p95;
    }

    /// <summary>9,900 finished documents and 50 of each waiting kind: the query must find a few among many.</summary>
    private static async Task LoadAsync(Container container)
    {
        var sentAt = DateTimeOffset.UtcNow.AddHours(-1);
        await Parallel.ForEachAsync(Enumerable.Range(0, Documents), new ParallelOptions { MaxDegreeOfParallelism = Writers }, async (i, token) =>
        {
            var batch = Row(i, sentAt);
            await container.CreateItemAsync(batch, new PartitionKey(batch.FarmId), cancellationToken: token);
        });
    }

    /// <summary>Batch number i of the load: the first 50 of each waiting kind, the rest finished.</summary>
    public static Batch Row(int i, DateTimeOffset sentAt)
    {
        var batch = Closed(i);
        if (i < Waiting) return batch with { Pending = Event(i, PendingStatus.Pending, null) };
        return i < 2 * Waiting ? batch with { Pending = Event(i, PendingStatus.Sent, sentAt) } : batch;
    }

    private static PendingEvent Event(int i, string status, DateTimeOffset? sentAt) =>
        new() { EventId = $"{i}:Closed", TraceId = "t", Status = status, SentAt = sentAt };

    private static Batch Closed(int i)
    {
        var at = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString();
        var batch = BatchMachine.Create(id, DemoUsers.KnownFarms[i % DemoUsers.KnownFarms.Count], $"Product {i}", new Stamp("ada", "t", at));
        batch = HashChain.Seal(batch, "r0");
        foreach (var state in new[] { BatchState.Dose1, BatchState.Dose2 })
            batch = HashChain.Seal(BatchMachine.Advance(batch, state, new Stamp("ada", "t", at)).Batch!, "r");
        batch = HashChain.Seal(BatchMachine.Inspect(batch, true, null, new Stamp("ada", "t", at)).Batch!, "r");
        foreach (var state in new[] { BatchState.Shipped, BatchState.Closed })
            batch = HashChain.Seal(BatchMachine.Advance(batch, state, new Stamp("ada", "t", at)).Batch!, "r");
        return batch;
    }
}
