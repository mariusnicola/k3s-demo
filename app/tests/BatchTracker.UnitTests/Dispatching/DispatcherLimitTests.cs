using BatchTracker.Application.Dispatching;
using BatchTracker.Application;
using BatchTracker.Domain;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

/// <summary>Deliveries and dead letters each have their own limit, so raising one moves no other.</summary>
public sealed class DispatcherLimitTests : DispatcherRunnerTest
{
    private void AddPendingRows(int count)
    {
        for (var i = 0; i < count; i++)
            store.Put(In(BatchState.Created, $"s-{i}") with { Pending = new PendingEvent { EventId = $"s-{i}", TraceId = "t", Status = PendingStatus.Pending } });
    }

    [Fact]
    public void REQ_BT_009_Deliveries_run_40_at_once_4_for_each_of_10_Workers()
    {
        Assert.Equal(40, new DispatcherOptions().DeliveryConcurrency);
    }

    [Fact]
    public async Task REQ_BT_009_Deliveries_keep_to_their_limit()
    {
        options = options with { DeliveryConcurrency = 3 };
        var busy = 0;
        var most = 0;
        worker.Handler = async (_, token) =>
        {
            var now = Interlocked.Increment(ref busy);
            lock (errors) most = Math.Max(most, now);
            await Task.Delay(5, token);
            Interlocked.Decrement(ref busy);
            return WorkerResult.Done;
        };
        Queue(30);
        Run();
        leadership.Gain();

        await Until(() => Completed == 30, "all delivered");

        Assert.Equal(3, most);
    }

    [Fact]
    public async Task REQ_BT_021_The_dead_letter_read_stays_at_1_whatever_the_other_limits_are()
    {
        options = options with { DeliveryConcurrency = 3 };
        Run();
        leadership.Gain();

        await Until(() => bus.DeadLetterRead > 0, "a dead letter read");

        Assert.Equal(1, bus.DeadLetterRead);
    }

    [Fact]
    public async Task REQ_BT_009_A_full_list_at_the_new_limit_sends_each_event_once()
    {
        AddPendingRows(200);
        bus.EchoSends = false;
        await using var queue = await bus.ConnectAsync(default);

        await new Outbox(store, hops, clock, new DispatcherOptions()).SweepAsync(queue, default);

        Assert.Equal(200, bus.Sent.Count);
        Assert.Equal(200, bus.Sent.Select(e => e.EventId).Distinct().Count());
        Assert.Equal(1, bus.BatchSends);
    }

    [Fact]
    public void REQ_BT_009_A_round_is_200_ms_and_200_rows()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(200), new DispatcherOptions().SweepInterval);
        Assert.Equal(200, new DispatcherOptions().SweepSize);
    }
}
