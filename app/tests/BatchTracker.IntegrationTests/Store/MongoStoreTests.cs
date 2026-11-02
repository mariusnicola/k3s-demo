using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using MongoDB.Driver;

namespace BatchTracker.IntegrationTests;

/// <summary>The contract on the store the services run on.</summary>
public sealed class MongoStoreTests(Stack stack) : StoreContract(stack)
{
    private readonly IMongoClient client = MongoBatchStore.CreateClient(Env.Mongo);
    private MongoBatchStore? store;

    protected override IBatchStore Store => store!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await client.GetDatabase(Env.Database).DropCollectionAsync(Env.Container);
        store = new MongoBatchStore(client, Env.Database, Env.Container, TimeProvider.System);
    }

    /// <summary>The bulk write behind the bundle route, on Mongo: a lost If-Match and a duplicate id are refused, the other rows are saved.</summary>
    [Fact]
    public async Task REQ_BT_031_One_mongo_write_refuses_a_lost_if_match_and_a_duplicate_and_saves_the_rest()
    {
        var stamp = new Stamp("ada", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        Batch Seed() => BatchMachine.Create(Guid.NewGuid().ToString(), "farm-a", "Vaccine", stamp with { TraceId = Guid.NewGuid().ToString("N") }) with { Pending = null };
        var moved = (await Store.CreateAsync(Seed(), default))!;
        var kept = (await Store.CreateAsync(Seed(), default))!;
        var held = (await Store.CreateAsync(Seed(), default))!;
        var fresh = BatchMachine.Create(Guid.NewGuid().ToString(), "farm-a", "Vaccine", stamp with { TraceId = Guid.NewGuid().ToString("N") });
        var taken = BatchMachine.Create(held.Batch.Id, "farm-a", "Vaccine", stamp with { TraceId = Guid.NewGuid().ToString("N") });

        var saved = await Store.WriteManyAsync(
        [
            new BundleWrite(BatchMachine.Advance(moved.Batch, BatchState.Dose1, stamp with { TraceId = Guid.NewGuid().ToString("N") }).Batch!, "\"999\""),
            new BundleWrite(BatchMachine.Advance(kept.Batch, BatchState.Dose1, stamp with { TraceId = Guid.NewGuid().ToString("N") }).Batch!, kept.ETag),
            new BundleWrite(fresh, null),
            new BundleWrite(taken, null)
        ], default);

        Assert.Null(saved[0]);
        Assert.Equal(BatchState.Dose1, saved[1]!.Batch.State);
        Assert.Equal(fresh.Id, saved[2]!.Batch.Id);
        Assert.Null(saved[3]);
        Assert.Equal(BatchState.Created, (await Store.GetAsync("farm-a", moved.Batch.Id, default))!.Batch.State);
        Assert.Equal(BatchState.Dose1, (await Store.GetAsync("farm-a", kept.Batch.Id, default))!.Batch.State);
        Assert.NotNull(await Store.GetAsync("farm-a", fresh.Id, default));
    }

    public override Task DisposeAsync()
    {
        client.Dispose();
        return Task.CompletedTask;
    }
}
