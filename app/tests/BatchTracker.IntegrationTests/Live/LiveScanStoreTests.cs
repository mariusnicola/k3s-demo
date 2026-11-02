using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.IntegrationTests;

/// <summary>The scan against a real store: its rows stay bounded however many batches exist.</summary>
public class LiveScanStoreTests(Stack stack) : StackTest(stack)
{
    private const string Farm = "farm-a";
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    private static Batch Settled(int age, BatchState state = BatchState.Created) => new()
    {
        Id = Guid.NewGuid().ToString(),
        FarmId = Farm,
        Product = "p",
        State = state,
        CreatedAt = Start.AddDays(10_000 - age)
    };

    private Task WriteAsync(IEnumerable<Batch> batches) =>
        Task.WhenAll(batches.Select(b => Stack.Store.CreateAsync(b, default)));

    [Fact]
    public async Task REQ_BT_028_Rows_stop_at_the_newest_of_a_farm_and_the_counts_do_not()
    {
        await WriteAsync(Enumerable.Range(0, LiveScan.RowsPerFarm + 5).Select(i => Settled(i)));

        var scan = await Stack.Store.FindForLiveAsync(default);

        Assert.Equal(LiveScan.RowsPerFarm, scan.Rows.Count);
        Assert.Equal(LiveScan.RowsPerFarm + 5, Assert.Single(scan.Counts).Count);
    }

    [Fact]
    public async Task REQ_BT_028_The_rows_kept_are_the_newest()
    {
        var oldest = Settled(LiveScan.RowsPerFarm);
        await WriteAsync([oldest, .. Enumerable.Range(0, LiveScan.RowsPerFarm).Select(i => Settled(i))]);

        var scan = await Stack.Store.FindForLiveAsync(default);

        Assert.DoesNotContain(scan.Rows, r => r.Batch.Id == oldest.Id);
    }

    [Fact]
    public async Task REQ_BT_028_An_old_pending_or_flagged_row_is_kept()
    {
        var pending = Settled(5000) with { Pending = new PendingEvent { EventId = "e", TraceId = "t", Status = PendingStatus.Sent } };
        var flagged = Settled(5001) with { Flags = [new Flag("Overdue", Start)] };
        await WriteAsync([pending, flagged, .. Enumerable.Range(0, LiveScan.RowsPerFarm).Select(i => Settled(i))]);

        var scan = await Stack.Store.FindForLiveAsync(default);

        Assert.Contains(scan.Rows, r => r.Batch.Id == pending.Id);
        Assert.Contains(scan.Rows, r => r.Batch.Id == flagged.Id);
        Assert.Equal(LiveScan.RowsPerFarm + 2, scan.Rows.Count);
    }

    [Fact]
    public async Task REQ_BT_028_A_row_both_open_and_new_comes_once()
    {
        await WriteAsync([Settled(0) with { Flags = [new Flag("Overdue", Start)] }]);

        var scan = await Stack.Store.FindForLiveAsync(default);

        Assert.Single(scan.Rows);
    }

    [Fact]
    public async Task REQ_BT_028_Counts_split_by_farm_and_state()
    {
        await WriteAsync([Settled(0), Settled(1), Settled(2, BatchState.Dose1), Settled(3) with { FarmId = "farm-b" }]);

        var counts = (await Stack.Store.FindForLiveAsync(default)).Counts;

        Assert.Contains(new StateCount(Farm, "Created", 2), counts);
        Assert.Contains(new StateCount(Farm, "Dose1", 1), counts);
        Assert.Contains(new StateCount("farm-b", "Created", 1), counts);
    }

    [Fact]
    public async Task REQ_BT_028_Too_many_open_rows_are_capped_and_say_so()
    {
        using var store = new CosmosBatchStore(Stack.Cosmos, Env.Database, Env.Container, maxOpen: 2);
        await WriteAsync(Enumerable.Range(0, 3).Select(i => Settled(i + 5000) with { Flags = [new Flag("Overdue", Start)] }));
        await WriteAsync(Enumerable.Range(0, LiveScan.RowsPerFarm).Select(i => Settled(i)));

        var scan = await store.FindForLiveAsync(default);

        Assert.True(scan.Capped);
        Assert.Equal(LiveScan.RowsPerFarm + 2, scan.Rows.Count);
    }

    /// <summary>A row whose event died, which only a person can move on.</summary>
    private static Batch Blocked(int age) => Settled(age) with
    {
        Pending = new PendingEvent { EventId = "e", TraceId = "t", Status = PendingStatus.Dead }
    };

    /// <summary>A running row with a flag and nothing pending.</summary>
    private static Batch Flagged(int age) => Settled(age) with { Flags = [new Flag("Overdue", Start)] };

    /// <summary>Enough new rows that only the open query can still find an old one.</summary>
    private static IEnumerable<Batch> Newer() => Enumerable.Range(0, LiveScan.RowsPerFarm).Select(i => Settled(i));

    /// <summary>A store whose cap is small enough to reach.</summary>
    private static CosmosBatchStore Capped(Stack stack, int max) => new(stack.Cosmos, Env.Database, Env.Container, maxOpen: max);

    [Fact]
    public async Task B_026_Flags_never_push_out_a_blocked_row()
    {
        using var store = Capped(Stack, 2);
        var blocked = Blocked(9000);
        await WriteAsync([blocked, Flagged(1), Flagged(2), Flagged(3), .. Newer()]);

        var scan = await store.FindForLiveAsync(default);

        Assert.Contains(scan.Rows, r => r.Batch.Id == blocked.Id);
        Assert.True(scan.Capped);
    }

    [Fact]
    public async Task B_026_Flags_never_push_out_a_pending_row()
    {
        using var store = Capped(Stack, 2);
        var pending = Settled(9000) with { Pending = new PendingEvent { EventId = "e", TraceId = "t", Status = PendingStatus.Sent } };
        await WriteAsync([pending, Flagged(1), Flagged(2), Flagged(3), .. Newer()]);

        var scan = await store.FindForLiveAsync(default);

        Assert.Contains(scan.Rows, r => r.Batch.Id == pending.Id);
    }

    [Fact]
    public async Task B_026_The_newest_flags_stay_at_the_cap()
    {
        using var store = Capped(Stack, 2);
        var newest = Flagged(9000);
        var oldest = Flagged(9002);
        await WriteAsync([.. Enumerable.Range(0, LiveScan.RowsPerFarm).Select(i => Settled(i)), oldest, Flagged(9001), newest]);

        var scan = await store.FindForLiveAsync(default);

        Assert.Contains(scan.Rows, r => r.Batch.Id == newest.Id);
        Assert.DoesNotContain(scan.Rows, r => r.Batch.Id == oldest.Id);
    }

    [Fact]
    public async Task B_026_An_old_finished_flagged_row_is_not_open()
    {
        var closed = Flagged(9000) with { State = BatchState.Closed };
        var rejected = Flagged(9001) with { State = BatchState.Rejected };
        await WriteAsync([closed, rejected, .. Enumerable.Range(0, LiveScan.RowsPerFarm).Select(i => Settled(i))]);

        var scan = await Stack.Store.FindForLiveAsync(default);

        Assert.Equal(LiveScan.RowsPerFarm, scan.Rows.Count);
    }

    [Fact]
    public async Task B_037_Exactly_the_cap_leaves_nothing_out_so_it_is_not_capped()
    {
        using var store = Capped(Stack, 2);
        await WriteAsync([Blocked(1), Blocked(2)]);

        Assert.False((await store.FindForLiveAsync(default)).Capped);
    }

    [Fact]
    public async Task B_037_The_cap_met_across_two_kinds_of_row_is_not_capped()
    {
        using var store = Capped(Stack, 2);
        await WriteAsync([Blocked(1), Flagged(2)]);

        Assert.False((await store.FindForLiveAsync(default)).Capped);
    }

    [Fact]
    public async Task B_037_One_row_past_the_cap_is_capped_and_the_newest_blocked_row_goes()
    {
        using var store = Capped(Stack, 2);
        var (gone, kept, oldest) = (Blocked(9000), Blocked(9001), Blocked(9002));
        await WriteAsync([gone, kept, oldest, .. Newer()]);

        var scan = await store.FindForLiveAsync(default);

        Assert.True(scan.Capped);
        Assert.Equal(LiveScan.RowsPerFarm + 2, scan.Rows.Count);
        Assert.DoesNotContain(scan.Rows, r => r.Batch.Id == gone.Id);
        Assert.Contains(scan.Rows, r => r.Batch.Id == kept.Id);
        Assert.Contains(scan.Rows, r => r.Batch.Id == oldest.Id);
    }

    [Fact]
    public async Task REQ_BT_028_A_scan_under_the_cap_is_not_capped()
    {
        await WriteAsync([Settled(0)]);

        Assert.False((await Stack.Store.FindForLiveAsync(default)).Capped);
    }
}
