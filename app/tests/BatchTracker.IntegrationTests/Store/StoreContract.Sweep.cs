using BatchTracker.Application;
using BatchTracker.Domain;

namespace BatchTracker.IntegrationTests;

/// <summary>What a sweep needs of a store: reads without the steps, and one mark at each row's ETag.</summary>
public abstract partial class StoreContract
{
    /// <summary>A real first step, so a read or a mark that touches the steps shows.</summary>
    private static Batch WithSteps(string farm = FarmA) =>
        BatchMachine.Create(Guid.NewGuid().ToString(), farm, "Vaccine", new Stamp("ada", "t", Noon)) with
        {
            Pending = new PendingEvent { EventId = "e", TraceId = "t", Status = PendingStatus.Pending }
        };

    [Fact]
    public async Task REQ_BT_009_A_sweep_read_carries_the_event_and_leaves_the_steps_behind()
    {
        var saved = await SaveAsync(WithSteps());

        var row = Assert.Single(await Store.FindByPendingStatusAsync(PendingStatus.Pending, 10, default));

        Assert.NotEmpty(saved.Batch.Steps);
        Assert.Empty(row.Batch.Steps);
        Assert.Equal(saved.Batch.Pending!.EventId, row.Batch.Pending!.EventId);
        Assert.Equal(saved.ETag, row.ETag);
    }

    [Fact]
    public async Task REQ_BT_020_A_stale_read_leaves_the_steps_behind_too()
    {
        var batch = WithSteps();
        var saved = await SaveAsync(batch with { Pending = batch.Pending! with { Status = PendingStatus.Sent, SentAt = Noon } });

        var row = Assert.Single(await Store.FindSentBeforeAsync(Noon.AddSeconds(1), 10, default));

        Assert.NotEmpty(saved.Batch.Steps);
        Assert.Empty(row.Batch.Steps);
    }

    [Fact]
    public async Task REQ_BT_009_One_mark_moves_each_row_at_its_etag_and_leaves_one_that_moved_on()
    {
        var kept = await SaveAsync(WithSteps());
        var moved = await SaveAsync(WithSteps(FarmB));
        var rows = await Store.FindByPendingStatusAsync(PendingStatus.Pending, 10, default);
        await Store.ReplaceAsync(moved.Batch, moved.ETag, default);

        var won = await Store.MarkSentAsync(rows, Noon, default);

        Assert.Equal(kept.Batch.Id, Assert.Single(won).Batch.Id);
        var marked = (await Store.GetAsync(FarmA, kept.Batch.Id, default))!;
        Assert.Equal((PendingStatus.Sent, Noon), (marked.Batch.Pending!.Status, marked.Batch.Pending.SentAt));
        Assert.NotEmpty(marked.Batch.Steps);
        Assert.Equal(kept.Batch.Steps, marked.Batch.Steps);
        Assert.Equal(PendingStatus.Pending, (await Store.GetAsync(FarmB, moved.Batch.Id, default))!.Batch.Pending!.Status);
        Assert.Null(await Store.ReplaceAsync(marked.Batch, kept.ETag, default));
        Assert.NotNull(await Store.ReplaceAsync(marked.Batch, marked.ETag, default));
    }
}
