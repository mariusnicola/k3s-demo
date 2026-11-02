using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

/// <summary>Who records which stage, failures included, because a failure missing from the timing would flatter it.</summary>
public class StageRecordingTests
{
    private static readonly DateTimeOffset StoreNow = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly BatchEvent Message = new("b-1:Dose1", "b-1", "farm-a", "t", "Dose1");

    private readonly FakeStore store = new() { ClockNow = StoreNow };
    private readonly FakeCounters counters = new();

    private EventRecorder Recorder() => new(store, new FakeHops(), new Clock(Start), counters, new StoreClock(store, new Clock(Start)));

    private ReportingBatchStore Reporting(IBatchCounters? seen) =>
        new(store, new QuietChanges(), new StoreClock(store, new Clock(Start)), seen);

    private Batch Awaiting(DateTimeOffset? storedAt = null, bool failNext = false)
    {
        var batch = BatchMachine.Advance(In(BatchState.Created), BatchState.Dose1, StampAt(1)).Batch!;
        return batch with { FailNext = failNext, Pending = batch.Pending! with { EventId = Message.EventId, StoredAt = storedAt } };
    }

    private double[] Seconds(string stage) => counters.Times.Where(t => t.Stage == stage).Select(t => t.Seconds).ToArray();

    [Fact]
    public async Task REQ_BT_059_Queue_is_store_time_stored_to_received()
    {
        store.Put(Awaiting(StoreNow.AddSeconds(-2)));

        await Recorder().HandleAsync(Message, default);

        Assert.Equal([2.0], Seconds(Stages.Queue));
    }

    [Fact]
    public async Task REQ_BT_059_A_refused_event_was_still_received_so_it_counts_in_queue_and_worker()
    {
        store.Put(Awaiting(StoreNow.AddSeconds(-2), failNext: true));

        Assert.Equal(RecordResult.Refused, await Recorder().HandleAsync(Message, default));

        Assert.Single(Seconds(Stages.Queue));
        Assert.Single(Seconds(Stages.Worker));
    }

    [Fact]
    public async Task REQ_BT_059_Queue_is_never_negative()
    {
        store.Put(Awaiting(StoreNow.AddSeconds(1)));

        await Recorder().HandleAsync(Message, default);

        Assert.Equal([0.0], Seconds(Stages.Queue));
    }

    [Fact]
    public async Task REQ_BT_059_An_event_stored_before_the_stamp_existed_has_no_queue_time()
    {
        store.Put(Awaiting());

        await Recorder().HandleAsync(Message, default);

        Assert.Empty(Seconds(Stages.Queue));
        Assert.Single(Seconds(Stages.Worker));
    }

    [Fact]
    public async Task REQ_BT_059_A_redelivery_nobody_waits_on_has_worker_time_and_no_queue_time()
    {
        store.Put(Awaiting(StoreNow) with { Pending = null });

        await Recorder().HandleAsync(Message, default);

        Assert.Empty(Seconds(Stages.Queue));
        Assert.Single(Seconds(Stages.Worker));
    }

    [Fact]
    public async Task REQ_BT_059_A_lost_write_retry_counts_one_queue_time()
    {
        store.Put(Awaiting(StoreNow));
        store.LoseEveryWrite = true;

        Assert.Equal(RecordResult.Contended, await Recorder().HandleAsync(Message, default));

        Assert.Single(Seconds(Stages.Queue));
    }

    [Fact]
    public async Task REQ_BT_059_A_handling_that_throws_is_timed()
    {
        store.Put(Awaiting(StoreNow));
        store.WritesFail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Recorder().HandleAsync(Message, default));

        Assert.Single(Seconds(Stages.Worker));
    }

    [Fact]
    public async Task REQ_BT_059_A_new_pending_is_stamped_on_the_store_clock()
    {
        var saved = await Reporting(counters).CreateAsync(Awaiting(), default);

        Assert.Equal(StoreNow, saved!.Batch.Pending!.StoredAt);
    }

    [Fact]
    public async Task REQ_BT_059_A_moved_event_keeps_its_first_stamp()
    {
        var row = store.Put(Awaiting(StoreNow.AddMinutes(-1)));
        var marked = row.Batch with { Pending = row.Batch.Pending! with { Status = PendingStatus.Sent } };

        var saved = await Reporting(counters).ReplaceAsync(marked, row.ETag, default);

        Assert.Equal(StoreNow.AddMinutes(-1), saved!.Batch.Pending!.StoredAt);
    }

    [Fact]
    public async Task REQ_BT_059_A_write_that_succeeds_is_timed()
    {
        await Reporting(counters).CreateAsync(Awaiting(), default);

        Assert.Single(Seconds(Stages.Store));
    }

    [Fact]
    public async Task REQ_BT_059_A_write_that_loses_its_match_is_timed()
    {
        var row = store.Put(Awaiting());
        store.LoseEveryWrite = true;

        Assert.Null(await Reporting(counters).ReplaceAsync(row.Batch, row.ETag, default));

        Assert.Single(Seconds(Stages.Store));
    }

    [Fact]
    public async Task REQ_BT_059_A_write_that_throws_is_timed()
    {
        var row = store.Put(Awaiting());
        store.WritesFail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Reporting(counters).ReplaceAsync(row.Batch, row.ETag, default));

        Assert.Single(Seconds(Stages.Store));
    }

    [Fact]
    public async Task REQ_BT_059_A_service_with_no_counters_still_writes()
    {
        Assert.NotNull(await Reporting(null).CreateAsync(Awaiting(), default));
    }

    [Fact]
    public async Task REQ_BT_059_A_replay_is_a_new_wait_so_it_loses_the_old_stamp()
    {
        var service = new BatchService(store, new FakeHops(), new Clock(Start), counters);
        var id = Guid.NewGuid().ToString();
        store.Put(Awaiting() with { Id = id, Pending = Awaiting().Pending! with { Status = PendingStatus.Dead, StoredAt = StoreNow.AddHours(-1) } });

        var outcome = await service.ReplayAsync(DemoUsers.Find("ada")!, "farm-a", id, default);

        Assert.Null(outcome.Detail);
        Assert.Null(store.Read(id).Pending!.StoredAt);
    }

    private sealed class QuietChanges : IChangeReporter
    {
        public void Report(Versioned row)
        {
        }
    }
}
