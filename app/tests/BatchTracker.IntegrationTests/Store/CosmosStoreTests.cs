using BatchTracker.Application;
using BatchTracker.Infrastructure;

namespace BatchTracker.IntegrationTests;

/// <summary>Keeps the Cosmos path proved after the default store moves on.</summary>
public sealed class CosmosStoreTests(Stack stack) : StoreContract(stack)
{
    private readonly CosmosBatchStore store = new(stack.Cosmos, Env.Database, Env.Container);

    protected override IBatchStore Store => store;

    public override Task DisposeAsync()
    {
        store.Dispose();
        return Task.CompletedTask;
    }
}
