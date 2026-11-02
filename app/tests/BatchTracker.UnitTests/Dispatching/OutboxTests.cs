using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

public class OutboxTests
{
    private readonly FakeStore store = new();
    private readonly FakeHops hops = new();
    private readonly FakeBus bus = new() { EchoSends = false };
    private readonly Clock clock = new(Start);
    private readonly DispatcherOptions options = new();

    private Outbox NewOutbox() => new(store, hops, clock, options);

    private Batch PendingBatch(string id = "b-1", string status = PendingStatus.Pending, DateTimeOffset? sentAt = null) =>
        In(BatchState.Created, id) with
        {
            Pending = new PendingEvent { EventId = $"{id}:Created", TraceId = "t", Status = status, SentAt = sentAt }
        };

    private async Task SweepAsync()
    {
        await using var queue = await bus.ConnectAsync(default);
        await NewOutbox().SweepAsync(queue, default);
    }

    private void PutPending(int count)
    {
        for (var i = 0; i < count; i++) store.Put(PendingBatch($"b-{i}"));
    }

    [Fact]
    public async Task REQ_BT_009_A_round_of_three_rows_is_one_send_of_three_events()
    {
        PutPending(3);

        await SweepAsync();

        Assert.Equal(1, bus.BatchSends);
        Assert.Equal(3, bus.Sent.Count);
    }

    [Fact]
    public async Task REQ_BT_009_A_sweep_that_marked_nothing_sends_nothing()
    {
        PutPending(options.SweepSize);
        store.LoseEveryWrite = true;

        await SweepAsync();

        Assert.Equal(0, bus.BatchSends);
    }

    [Fact]
    public async Task REQ_BT_009_A_full_list_is_one_store_write_and_one_send()
    {
        PutPending(options.SweepSize);

        await SweepAsync();

        Assert.Equal(1, store.Marks);
        Assert.Equal(1, bus.BatchSends);
        Assert.Equal(options.SweepSize, bus.Sent.Select(e => e.EventId).Distinct().Count());
    }

    [Fact]
    public async Task REQ_BT_009_A_sweep_with_nothing_due_writes_and_sends_nothing()
    {
        await SweepAsync();

        Assert.Equal(0, store.Marks);
        Assert.Equal(0, bus.BatchSends);
    }

    [Fact]
    public async Task REQ_BT_009_Only_rows_the_mark_won_are_sent()
    {
        PutPending(3);
        var lose = store.Read("b-1");
        store.BeforeReplace = _ =>
        {
            store.Put(lose with { FailNext = true });
            return Task.CompletedTask;
        };

        await SweepAsync();

        Assert.Equal(2, bus.Sent.Count);
        Assert.DoesNotContain(bus.Sent, e => e.BatchId == "b-1");
    }

    [Fact]
    public async Task REQ_BT_009_One_bad_event_fails_the_whole_send_and_none_goes()
    {
        PutPending(5);
        bus.FailSend = e => e.BatchId == "b-0";

        await Assert.ThrowsAsync<InvalidOperationException>(SweepAsync);

        Assert.Empty(bus.Sent);
    }

    [Fact]
    public async Task REQ_BT_020_A_bus_that_fails_a_send_leaves_the_round_marked_for_the_stale_sweep()
    {
        PutPending(options.SweepSize);
        bus.SendFails = true;

        await Assert.ThrowsAsync<InvalidOperationException>(SweepAsync);

        Assert.All(Enumerable.Range(0, options.SweepSize), i => Assert.Equal(PendingStatus.Sent, store.Read($"b-{i}").Pending!.Status));
    }

    [Fact]
    public async Task REQ_BT_009_One_outbox_reads_for_stale_rows_only_once_per_interval()
    {
        var outbox = NewOutbox();
        var queue = await bus.ConnectAsync(default);
        await outbox.SweepAsync(queue, default);
        store.Put(PendingBatch("stale", PendingStatus.Sent, clock.Now - options.ResendAfter - TimeSpan.FromSeconds(1)));

        clock.Now += options.StaleSweepEvery - TimeSpan.FromSeconds(1);
        await outbox.SweepAsync(queue, default);
        Assert.DoesNotContain(bus.Sent, e => e.BatchId == "stale");

        clock.Now += TimeSpan.FromSeconds(1);
        await outbox.SweepAsync(queue, default);
        Assert.Contains(bus.Sent, e => e.BatchId == "stale");
    }

    [Fact]
    public void REQ_BT_009_Stale_rows_are_read_every_30_s_and_an_idle_sweep_waits_at_most_4_s()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), options.StaleSweepEvery);
        Assert.Equal(TimeSpan.FromSeconds(4), options.SweepInterval * options.MaxIdleBackoff);
    }

    [Fact]
    public async Task REQ_BT_009_A_full_pending_list_does_not_hold_back_a_stale_row()
    {
        PutPending(options.SweepSize);
        store.Put(PendingBatch("stale", PendingStatus.Sent, clock.Now - options.ResendAfter - TimeSpan.FromSeconds(1)));

        await SweepAsync();

        Assert.Contains(bus.Sent, e => e.BatchId == "stale");
        Assert.Equal(1, bus.BatchSends);
    }

    [Fact]
    public async Task REQ_BT_009_Pending_event_is_marked_sent_then_sent_once_named_by_its_event_id()
    {
        store.Put(PendingBatch());

        await SweepAsync();

        var sent = Assert.Single(bus.Sent);
        Assert.Equal("b-1:Created", sent.EventId);
        Assert.Equal("farm-a", sent.FarmId);
        Assert.Equal(PendingStatus.Sent, store.Read("b-1").Pending!.Status);
        Assert.Equal(clock.Now, store.Read("b-1").Pending!.SentAt);
    }

    [Fact]
    public async Task REQ_BT_009_Second_sweep_sends_nothing_new()
    {
        store.Put(PendingBatch());

        await SweepAsync();
        await SweepAsync();

        Assert.Single(bus.Sent);
    }

    [Fact]
    public async Task REQ_BT_009_Mark_happens_before_send()
    {
        store.Put(PendingBatch());
        bus.SendFails = true;

        await Assert.ThrowsAsync<InvalidOperationException>(SweepAsync);

        Assert.Equal(PendingStatus.Sent, store.Read("b-1").Pending!.Status);
        Assert.Empty(bus.Sent);
    }

    [Fact]
    public async Task REQ_BT_009_Mark_that_loses_its_if_match_is_skipped()
    {
        store.Put(PendingBatch());
        store.BeforeReplace = _ =>
        {
            store.Put(store.Read("b-1") with { FailNext = true });
            return Task.CompletedTask;
        };

        await SweepAsync();

        Assert.Empty(bus.Sent);
        Assert.Equal(1, store.LostWrites);
    }

    [Fact]
    public async Task REQ_BT_020_Sent_older_than_120_seconds_is_resent_with_a_new_time()
    {
        store.Put(PendingBatch(status: PendingStatus.Sent, sentAt: clock.Now.AddSeconds(-121)));

        await SweepAsync();

        Assert.Single(bus.Sent);
        Assert.Equal(clock.Now, store.Read("b-1").Pending!.SentAt);
    }

    [Fact]
    public async Task REQ_BT_020_Sent_within_120_seconds_is_left_alone()
    {
        store.Put(PendingBatch(status: PendingStatus.Sent, sentAt: clock.Now.AddSeconds(-119)));

        await SweepAsync();

        Assert.Empty(bus.Sent);
    }

    [Fact]
    public async Task REQ_BT_020_Failed_send_is_healed_by_the_stale_sweep()
    {
        store.Put(PendingBatch());
        bus.SendFails = true;
        await Assert.ThrowsAsync<InvalidOperationException>(SweepAsync);

        bus.SendFails = false;
        clock.Now = clock.Now.AddSeconds(121);
        await SweepAsync();

        Assert.Single(bus.Sent);
    }

    [Fact]
    public async Task REQ_BT_011_Dead_event_is_never_sent()
    {
        store.Put(PendingBatch(status: PendingStatus.Dead, sentAt: clock.Now.AddHours(-1)));

        await SweepAsync();

        Assert.Empty(bus.Sent);
    }

    [Fact]
    public async Task REQ_BT_028_Sweep_reports_dispatcher_and_bus_hops_with_the_trace()
    {
        store.Put(PendingBatch());

        await SweepAsync();

        Assert.Equal([HopNames.Dispatcher, HopNames.Bus], hops.Hops.Select(h => h.Name));
        Assert.All(hops.Hops, h => Assert.Equal("t", h.TraceId));
    }

    [Fact]
    public async Task REQ_BT_009_Sweep_takes_at_most_its_size()
    {
        PutPending(options.SweepSize + 10);

        await SweepAsync();

        Assert.Equal(options.SweepSize, bus.Sent.Count);
    }
}
