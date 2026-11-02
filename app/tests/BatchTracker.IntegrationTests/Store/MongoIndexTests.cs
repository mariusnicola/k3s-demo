using System.Collections.Concurrent;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

namespace BatchTracker.IntegrationTests;

/// <summary>
/// Explains the commands the store really sent, caught off the wire, so a query and its proof cannot drift apart. Rows are
/// loaded first, because a plan on an empty collection proves nothing.
/// </summary>
[Collection(StackCollection.Name)]
public sealed class MongoIndexTests : IAsyncLifetime
{
    private const int Rows = 300;

    private readonly ConcurrentQueue<BsonDocument> sent = new();
    private readonly IMongoClient client;
    private readonly MongoBatchStore store;

    public MongoIndexTests()
    {
        var settings = MongoClientSettings.FromConnectionString(Env.Mongo);
        settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(e =>
        {
            if (e.CommandName is "find" or "aggregate") sent.Enqueue(BsonSerializer.Deserialize<BsonDocument>(e.Command.ToBson()));
        });
        client = new MongoClient(settings);
        store = new MongoBatchStore(client, Env.Database, Env.Container, TimeProvider.System);
    }

    public async Task InitializeAsync()
    {
        await client.GetDatabase(Env.Database).DropCollectionAsync(Env.Container);
        var stamp = new Stamp("ada", "t", DateTimeOffset.UtcNow);
        await Parallel.ForEachAsync(Enumerable.Range(0, Rows), async (i, token) =>
        {
            var batch = BatchMachine.Create(Guid.NewGuid().ToString(), DemoUsers.KnownFarms[i % 3], "Vaccine", stamp);
            var settled = batch with { Pending = null };
            await store.CreateAsync(i % 10 == 0 ? batch : i % 10 == 1 ? settled with { Flags = [new Flag("late", stamp.At)] } : settled, token);
        });
        sent.Clear();
    }

    public Task DisposeAsync()
    {
        store.Dispose();
        client.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Explain refuses everything the driver adds to a command.</summary>
    private static BsonDocument Bare(BsonDocument command)
    {
        var bare = command.DeepClone().AsBsonDocument;
        foreach (var name in bare.Names.Where(n => n.StartsWith('$') || n is "lsid" or "txnNumber").ToList()) bare.Remove(name);
        return bare;
    }

    /// <summary>A plan the server threw away never runs, so only the chosen one counts.</summary>
    private static BsonDocument Chosen(BsonDocument plan)
    {
        plan.Remove("rejectedPlans");
        foreach (var value in plan.Values.Where(v => v.IsBsonDocument || v.IsBsonArray)) ChosenIn(value);
        return plan;
    }

    private static void ChosenIn(BsonValue value)
    {
        if (value.IsBsonDocument) Chosen(value.AsBsonDocument);
        else foreach (var item in value.AsBsonArray.Where(i => i.IsBsonDocument)) Chosen(item.AsBsonDocument);
    }

    private async Task<List<string>> PlansAsync()
    {
        Assert.NotEmpty(sent);
        var plans = new List<string>();
        foreach (var command in sent)
        {
            var explain = new BsonDocument { { "explain", Bare(command) }, { "verbosity", "queryPlanner" } };
            plans.Add(Chosen(await client.GetDatabase(Env.Database).RunCommandAsync<BsonDocument>(explain)).ToJson());
        }
        return plans;
    }

    private async Task AssertEveryReadUsesAnIndexWithNoSortAsync()
    {
        foreach (var plan in await PlansAsync())
        {
            Assert.DoesNotContain("COLLSCAN", plan);
            Assert.Contains("IXSCAN", plan);
            Assert.DoesNotContain("\"SORT\"", plan);
        }
    }

    [Fact]
    public async Task REQ_BT_009_The_due_read_of_the_sweep_uses_an_index_and_needs_no_sort()
    {
        await store.FindByPendingStatusAsync(PendingStatus.Pending, 50, default);

        await AssertEveryReadUsesAnIndexWithNoSortAsync();
    }

    [Fact]
    public async Task REQ_BT_009_The_stale_read_of_the_sweep_uses_an_index_and_needs_no_sort()
    {
        await store.FindSentBeforeAsync(DateTimeOffset.UtcNow, 50, default);

        await AssertEveryReadUsesAnIndexWithNoSortAsync();
    }

    /// <summary>Summary and alerts run this same scan, so this holds for them as well.</summary>
    [Fact]
    public async Task REQ_BT_028_Every_read_of_the_live_scan_uses_an_index()
    {
        await store.FindForLiveAsync(default);

        await AssertEveryReadUsesAnIndexWithNoSortAsync();
    }

    [Fact]
    public async Task REQ_BT_028_The_state_count_reads_no_document()
    {
        await store.FindForLiveAsync(default);

        var plan = await PlanOfAsync(c => c.Contains("aggregate"));
        Assert.Contains("IXSCAN", plan);
        Assert.DoesNotContain("FETCH", plan);
    }

    /// <summary>The status index answers this read as well, but only by walking every settled row, so the index name is the proof.</summary>
    [Fact]
    public async Task REQ_BT_028_The_flag_read_runs_on_the_partial_index()
    {
        await store.FindForLiveAsync(default);

        Assert.Contains("\"indexName\" : \"createdAt_-1\"", await PlanOfAsync(c => c.ToJson().Contains("flags.0")));
    }

    private async Task<string> PlanOfAsync(Func<BsonDocument, bool> read)
    {
        var chosen = Assert.Single(sent.Where(read).ToList());
        sent.Clear();
        sent.Enqueue(chosen);
        return Assert.Single(await PlansAsync());
    }

    [Fact]
    public async Task REQ_BT_007_A_list_by_farm_and_by_state_uses_an_index()
    {
        await store.ListAsync(new BatchFilter(["farm-a"], BatchState.Created, 10), default);
        await store.ListAsync(new BatchFilter(null, BatchState.Created, 10), default);
        await store.ListAsync(new BatchFilter(null, null, 10), default);
        await store.ListAsync(new BatchFilter(["farm-a"], null, 10), default);
        await store.FindInStateAsync(BatchState.Created, default);

        await AssertEveryReadUsesAnIndexWithNoSortAsync();
    }
}
