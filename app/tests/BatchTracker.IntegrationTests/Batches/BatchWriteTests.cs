using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BatchTracker.IntegrationTests;

public class BatchWriteTests(Stack stack) : StackTest(stack)
{
    private sealed class Recording : IChangeReporter
    {
        public ConcurrentQueue<Versioned> Rows { get; } = new();

        public void Report(Versioned row) => Rows.Enqueue(row);
    }

    private ReportingBatchStore NewStore(Recording changes)
    {
        var store = new CosmosBatchStore(Stack.Cosmos, Env.Database, Env.Container);
        return new(store, changes, new StoreClock(store, TimeProvider.System), null);
    }

    private static Batch NewBatch() =>
        BatchMachine.Create(Guid.NewGuid().ToString(), "farm-a", "Vaccine", new Stamp("ada", "t", DateTimeOffset.UtcNow));

    [Fact]
    public async Task REQ_BT_028_Version_starts_at_one_and_rises_on_every_replace()
    {
        var olivia = As("olivia");
        var created = await olivia.Create();
        Assert.Equal(1, created.Batch.Version);

        var settled = await olivia.Settle(created.Batch);
        Assert.Equal(2, settled.Batch.Version);

        var moved = await olivia.Advance(settled.Batch, "Dose1", settled.ETag);
        Assert.Equal(3, (await ApiUser.ReadBatch(moved)).Version);
    }

    [Fact]
    public async Task REQ_BT_028_Document_written_before_the_field_reads_as_0_and_counts_on()
    {
        var created = await Stack.Batches.CreateItemAsync(
            new { id = Guid.NewGuid().ToString(), farmId = "farm-a", product = "Old", state = "Created", createdAt = DateTimeOffset.UtcNow },
            new Microsoft.Azure.Cosmos.PartitionKey("farm-a"));

        var old = (await Stack.Store.GetAsync("farm-a", created.Resource.id, default))!;
        var replaced = await Stack.Store.ReplaceAsync(old.Batch, old.ETag, default);

        Assert.Equal(0, old.Batch.Version);
        Assert.Equal(1, replaced!.Batch.Version);
    }

    [Fact]
    public async Task REQ_BT_028_Every_write_reports_the_row_it_stored_with_its_real_etag()
    {
        var changes = new Recording();
        var store = NewStore(changes);

        var created = (await store.CreateAsync(NewBatch(), default))!;
        var replaced = await store.ReplaceAsync(created.Batch, created.ETag, default);

        Assert.Equal([created.ETag, replaced!.ETag], changes.Rows.Select(r => r.ETag));
        Assert.Equal([1L, 2L], changes.Rows.Select(r => r.Batch.Version));
    }

    [Fact]
    public async Task REQ_BT_028_A_write_that_lost_its_if_match_reports_nothing()
    {
        var changes = new Recording();
        var store = NewStore(changes);
        var created = (await store.CreateAsync(NewBatch(), default))!;
        await store.ReplaceAsync(created.Batch, created.ETag, default);

        Assert.Null(await store.ReplaceAsync(created.Batch, created.ETag, default));

        Assert.Equal(2, changes.Rows.Count);
    }

    [Fact]
    public async Task REQ_BT_028_Projection_for_live_holds_no_steps_and_a_real_etag()
    {
        var row = await As("olivia").Create();

        var scan = await Stack.Store.FindForLiveAsync(default);

        var found = Assert.Single(scan.Rows);
        Assert.Equal(row.ETag, found.ETag);
        Assert.Empty(found.Batch.Steps);
        Assert.Equal(1, found.Batch.Version);
        Assert.Equal(row.Batch.Pending!.Status, found.Batch.Pending!.Status);
    }
}
