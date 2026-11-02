using System.Diagnostics;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using MongoDB.Driver;
using Xunit.Abstractions;

namespace BatchTracker.IntegrationTests;

/// <summary>The Cosmos budget, same numbers, on the capped Mongo container. Its own collection, dropped at the end.</summary>
[Collection(StackCollection.Name)]
public sealed class MongoQueryBudgetTests(ITestOutputHelper output)
{
    private const string CollectionName = "batches-budget";

    [Fact]
    public async Task Dispatcher_query_stays_under_200_ms_at_10000_documents_on_mongo()
    {
        using var client = MongoBatchStore.CreateClient(Env.Mongo);
        var database = client.GetDatabase(Env.Database);
        await database.DropCollectionAsync(CollectionName);
        using var store = new MongoBatchStore(client, Env.Database, CollectionName, TimeProvider.System);
        try
        {
            var loading = Stopwatch.StartNew();
            var sentAt = DateTimeOffset.UtcNow.AddHours(-1);
            await Parallel.ForEachAsync(
                Enumerable.Range(0, QueryBudgetTests.Documents),
                new ParallelOptions { MaxDegreeOfParallelism = QueryBudgetTests.Writers },
                async (i, token) => await store.CreateAsync(QueryBudgetTests.Row(i, sentAt), token));
            output.WriteLine($"loaded {QueryBudgetTests.Documents} documents with {QueryBudgetTests.Writers} writers in {loading.Elapsed.TotalSeconds:F1} s");

            var pending = await QueryBudgetTests.MeasureAsync(output, "pending TOP 50", () =>
                store.FindByPendingStatusAsync(PendingStatus.Pending, 50, default), QueryBudgetTests.Waiting);
            var stale = await QueryBudgetTests.MeasureAsync(output, "stale sent TOP 50", () =>
                store.FindSentBeforeAsync(DateTimeOffset.UtcNow.AddSeconds(-120), 50, default), QueryBudgetTests.Waiting);

            Assert.True(pending < QueryBudgetTests.BudgetMs, $"pending query p95 {pending:F1} ms");
            Assert.True(stale < QueryBudgetTests.BudgetMs, $"stale query p95 {stale:F1} ms");
        }
        finally
        {
            await database.DropCollectionAsync(CollectionName);
        }
    }
}
