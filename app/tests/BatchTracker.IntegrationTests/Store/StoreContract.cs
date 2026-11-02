using BatchTracker.Application;
using BatchTracker.Domain;

namespace BatchTracker.IntegrationTests;

/// <summary>What any store behind IBatchStore promises, run on each one, so the store can change and the promises cannot.</summary>
public abstract partial class StoreContract(Stack stack) : StackTest(stack)
{
    private const string FarmA = "farm-a";
    private const string FarmB = "farm-b";
    private static readonly DateTimeOffset Noon = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Longer than the second the coarsest store orders writes by.</summary>
    private static readonly TimeSpan WriteGap = TimeSpan.FromMilliseconds(1100);

    /// <summary>The store under test, over an empty test database.</summary>
    protected abstract IBatchStore Store { get; }

    private static Batch New(string farm = FarmA, string? id = null, BatchState state = BatchState.Created) => new()
    {
        Id = id ?? Guid.NewGuid().ToString(),
        FarmId = farm,
        Product = "Vaccine",
        State = state,
        CreatedAt = Noon
    };

    private static Batch With(string status, DateTimeOffset? sentAt = null, string farm = FarmA) => New(farm) with
    {
        Pending = new PendingEvent { EventId = "e", TraceId = "t", Status = status, SentAt = sentAt }
    };

    private async Task<Versioned> SaveAsync(Batch batch) => (await Store.CreateAsync(batch, default))!;

    [Fact]
    public async Task REQ_BT_059_The_store_answers_with_its_own_clock()
    {
        var clock = await ((IStoreReadings)Store).ClockAsync(default);

        Assert.InRange(clock, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task REQ_BT_059_In_progress_is_pending_and_sent_across_farms_and_not_dead()
    {
        await SaveAllAsync(With(PendingStatus.Pending), With(PendingStatus.Sent, farm: FarmB), With(PendingStatus.Dead), New());

        Assert.Equal(2, await ((IStoreReadings)Store).CountInProgressAsync(default));
    }

    [Fact]
    public async Task REQ_BT_059_The_stored_time_survives_the_store()
    {
        var stored = Noon.AddSeconds(1.5);
        var saved = await SaveAsync(New() with { Pending = new PendingEvent { EventId = "e", TraceId = "t", Status = PendingStatus.Pending, StoredAt = stored } });

        var read = await Store.GetAsync(FarmA, saved.Batch.Id, default);

        Assert.Equal(stored, read!.Batch.Pending!.StoredAt);
    }

    private async Task SaveAllAsync(params Batch[] batches)
    {
        foreach (var batch in batches) await SaveAsync(batch);
    }

    [Fact]
    public async Task REQ_BT_004_Create_starts_at_version_1_and_reads_back_with_the_same_etag()
    {
        var saved = await SaveAsync(New());

        var read = await Store.GetAsync(FarmA, saved.Batch.Id, default);

        Assert.Equal(1, saved.Batch.Version);
        Assert.Equal(1, read!.Batch.Version);
        Assert.Equal(saved.ETag, read.ETag);
    }

    [Fact]
    public async Task REQ_BT_004_A_second_create_of_an_id_returns_null()
    {
        var saved = await SaveAsync(New());

        Assert.Null(await Store.CreateAsync(New(id: saved.Batch.Id), default));
    }

    [Fact]
    public async Task REQ_BT_004_One_id_can_live_in_two_farms()
    {
        var id = Guid.NewGuid().ToString();
        await SaveAsync(New(FarmA, id));

        Assert.NotNull(await Store.CreateAsync(New(FarmB, id), default));

        Assert.Equal(FarmA, (await Store.GetAsync(FarmA, id, default))!.Batch.FarmId);
        Assert.Equal(FarmB, (await Store.GetAsync(FarmB, id, default))!.Batch.FarmId);
    }

    [Fact]
    public async Task REQ_BT_007_Get_of_another_farm_or_a_missing_id_is_null()
    {
        var saved = await SaveAsync(New(FarmA));

        Assert.Null(await Store.GetAsync(FarmB, saved.Batch.Id, default));
        Assert.Null(await Store.GetAsync(FarmA, Guid.NewGuid().ToString(), default));
    }

    [Fact]
    public async Task REQ_BT_006_Replace_on_the_held_etag_raises_the_version_and_moves_the_etag()
    {
        var saved = await SaveAsync(New());

        var replaced = await Store.ReplaceAsync(saved.Batch with { Product = "Changed" }, saved.ETag, default);

        Assert.Equal(2, replaced!.Batch.Version);
        Assert.NotEqual(saved.ETag, replaced.ETag);
        var read = await Store.GetAsync(FarmA, saved.Batch.Id, default);
        Assert.Equal("Changed", read!.Batch.Product);
        Assert.Equal(replaced.ETag, read.ETag);
    }

    [Fact]
    public async Task REQ_BT_006_Replace_on_a_stale_etag_is_null_and_writes_nothing()
    {
        var saved = await SaveAsync(New());
        await Store.ReplaceAsync(saved.Batch, saved.ETag, default);

        var lost = await Store.ReplaceAsync(saved.Batch with { Product = "Lost" }, saved.ETag, default);

        Assert.Null(lost);
        Assert.Equal("Vaccine", (await Store.GetAsync(FarmA, saved.Batch.Id, default))!.Batch.Product);
    }

    [Fact]
    public async Task REQ_BT_006_The_etag_of_a_listed_row_is_good_for_a_replace()
    {
        await SaveAsync(New());

        var row = Assert.Single(await Store.ListAsync(new BatchFilter([FarmA], null, 10), default));

        Assert.NotNull(await Store.ReplaceAsync(row.Batch, row.ETag, default));
    }

    [Fact]
    public async Task REQ_BT_012_A_sealed_chain_read_back_still_verifies_to_the_tick()
    {
        var at = Noon.AddTicks(1234567);
        var saved = await SaveAsync(BatchMachine.Create(Guid.NewGuid().ToString(), FarmA, "Vaccine", new Stamp("ada", "t", at)));
        await Store.ReplaceAsync(HashChain.Seal(saved.Batch, "record"), saved.ETag, default);

        var read = (await Store.GetAsync(FarmA, saved.Batch.Id, default))!.Batch;

        Assert.Equal(at, read.Steps[0].At);
        Assert.Equal(at, read.CreatedAt);
        Assert.True(HashChain.Verify(read));
    }

    [Fact]
    public async Task REQ_BT_007_List_filters_by_farm_and_state_newest_first_up_to_the_limit()
    {
        var oldest = New() with { CreatedAt = Noon };
        var middle = New() with { CreatedAt = Noon.AddSeconds(1) };
        var newest = New() with { CreatedAt = Noon.AddSeconds(2) };
        await SaveAllAsync(oldest, newest, middle, New(FarmB), New(state: BatchState.Dose1));

        var rows = await Store.ListAsync(new BatchFilter([FarmA], BatchState.Created, 2), default);

        Assert.Equal([newest.Id, middle.Id], rows.Select(r => r.Batch.Id));
    }

    [Fact]
    public async Task REQ_BT_009_Pending_rows_come_oldest_written_first_across_farms_up_to_the_limit()
    {
        var first = await SaveAsync(With(PendingStatus.Pending, farm: FarmB));
        await Task.Delay(WriteGap);
        var second = await SaveAsync(With(PendingStatus.Pending));
        await SaveAsync(With(PendingStatus.Sent, Noon));

        var both = await Store.FindByPendingStatusAsync(PendingStatus.Pending, 10, default);
        var one = await Store.FindByPendingStatusAsync(PendingStatus.Pending, 1, default);

        Assert.Equal([first.Batch.Id, second.Batch.Id], both.Select(r => r.Batch.Id));
        Assert.Equal(first.Batch.Id, Assert.Single(one).Batch.Id);
    }

    [Fact]
    public async Task REQ_BT_009_A_rewrite_sends_a_pending_row_behind_the_ones_waiting()
    {
        var first = await SaveAsync(With(PendingStatus.Pending));
        await Task.Delay(WriteGap);
        var second = await SaveAsync(With(PendingStatus.Pending));
        await Task.Delay(WriteGap);
        await Store.ReplaceAsync(first.Batch, first.ETag, default);

        var rows = await Store.FindByPendingStatusAsync(PendingStatus.Pending, 10, default);

        Assert.Equal([second.Batch.Id, first.Batch.Id], rows.Select(r => r.Batch.Id));
    }

    [Fact]
    public async Task REQ_BT_020_Sent_before_compares_as_time_across_fraction_lengths()
    {
        var whole = With(PendingStatus.Sent, Noon);
        var tenths = With(PendingStatus.Sent, Noon.AddMilliseconds(500), FarmB);
        var hundredths = With(PendingStatus.Sent, Noon.AddMilliseconds(120));
        await SaveAllAsync(tenths, hundredths, whole, With(PendingStatus.Pending));

        var rows = await Store.FindSentBeforeAsync(Noon.AddMilliseconds(200), 10, default);
        var one = await Store.FindSentBeforeAsync(Noon.AddMilliseconds(200), 1, default);

        Assert.Equal([whole.Id, hundredths.Id], rows.Select(r => r.Batch.Id));
        Assert.Equal(whole.Id, Assert.Single(one).Batch.Id);
    }

    [Fact]
    public async Task REQ_BT_013_Find_in_state_holds_only_that_state_across_farms()
    {
        var a = New(FarmA, state: BatchState.Dose1);
        var b = New(FarmB, state: BatchState.Dose1);
        await SaveAllAsync(a, b, New());

        var rows = await Store.FindInStateAsync(BatchState.Dose1, default);

        Assert.Equal(new[] { a.Id, b.Id }.Order(), rows.Select(r => r.Batch.Id).Order());
    }

    [Fact]
    public async Task REQ_BT_016_Reports_read_only_the_farms_named_and_null_reads_every_farm()
    {
        var a = New(FarmA);
        var b = New(FarmB);
        await SaveAllAsync(a, b);

        Assert.Equal(a.Id, Assert.Single(await Store.FindForReportsAsync([FarmA], default)).Id);
        Assert.Equal(2, (await Store.FindForReportsAsync(null, default)).Count);
    }

    [Fact]
    public async Task REQ_BT_028_The_live_scan_counts_rows_holds_no_steps_and_keeps_a_good_etag()
    {
        await SaveAsync(BatchMachine.Create(Guid.NewGuid().ToString(), FarmA, "Vaccine", new Stamp("ada", "t", Noon)));
        await SaveAsync(New(FarmB));

        var scan = await Store.FindForLiveAsync(default);

        Assert.Equal(new[] { (FarmA, 1), (FarmB, 1) }, scan.Counts.Select(c => (c.FarmId, c.Count)).Order());
        var row = Assert.Single(scan.Rows, r => r.Batch.FarmId == FarmA);
        Assert.Empty(row.Batch.Steps);
        var held = await Store.GetAsync(FarmA, row.Batch.Id, default);
        Assert.Equal(held!.ETag, row.ETag);
    }

    [Fact]
    public async Task REQ_BT_027_A_running_store_is_reachable()
    {
        Assert.True(await Store.ReachableAsync(default));
    }
}
