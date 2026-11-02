using System.Collections.Concurrent;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;

namespace BatchTracker.UnitTests;

/// <summary>Deliveries hold a slot each, so one slow message never holds back the rest.</summary>
public sealed class DispatcherDeliveryTests : DispatcherRunnerTest
{
    [Fact]
    public async Task REQ_BT_009_A_slow_delivery_holds_back_no_other()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Handler = async (message, _) =>
        {
            if (message.BatchId == "b-0") await release.Task;
            return WorkerResult.Done;
        };
        Queue(30);
        Run();
        leadership.Gain();

        await Until(() => Completed == 29, "the other 29 delivered");

        release.SetResult();
        await Until(() => Completed == 30, "the slow one delivered");
    }

    [Fact]
    public async Task REQ_BT_009_At_most_concurrency_deliveries_run_at_once()
    {
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
        options = options with { DeliveryConcurrency = 8 };
        Queue(60);
        Run();
        leadership.Gain();

        await Until(() => Completed == 60, "all delivered");

        Assert.Equal(options.DeliveryConcurrency, most);
    }

    [Fact]
    public async Task REQ_BT_028_A_delivery_reports_a_deliver_hop_as_it_leaves_the_queue()
    {
        var hopsAtPost = -1;
        worker.Handler = (_, _) =>
        {
            hopsAtPost = hops.Hops.Count(h => h.Name == HopNames.Deliver);
            return Task.FromResult(WorkerResult.Done);
        };
        Queue(1);
        Run();
        leadership.Gain();

        await Until(() => Completed == 1, "delivered");

        Assert.Equal(1, hopsAtPost);
        var hop = Assert.Single(hops.Hops, h => h.Name == "deliver");
        Assert.Equal(("t", "b-0", "farm-a", "Created", true), (hop.TraceId, hop.BatchId, hop.FarmId, hop.State, hop.Ok));
        Assert.Equal(clock.GetUtcNow(), hop.At);
    }

    [Fact]
    public async Task REQ_BT_028_A_redelivery_counts_as_a_delivery_again()
    {
        var tries = 0;
        worker.Handler = (_, _) => Task.FromResult(Interlocked.Increment(ref tries) == 1 ? WorkerResult.Unavailable : WorkerResult.Done);
        Queue(1);
        Run();
        leadership.Gain();

        await Until(() => Completed == 2, "second try delivered");

        Assert.Equal(2, hops.Hops.Count(h => h.Name == HopNames.Deliver));
    }

    [Fact]
    public async Task REQ_BT_021_A_failed_event_goes_back_alone_and_the_rest_stay_done()
    {
        options = options with { WorkerFailuresToStop = 3 };
        bus.EchoSends = false;
        var posts = new ConcurrentQueue<string>();
        worker.Handler = (message, _) =>
        {
            posts.Enqueue(message.BatchId);
            return Task.FromResult(message.BatchId == "b-1" ? WorkerResult.Unavailable : WorkerResult.Done);
        };
        Queue(3);
        Run();
        leadership.Gain();

        await Until(() => Completed == 3, "the whole pack settled");

        Assert.Equal(3, posts.Count);
        Assert.Equal(1, store.Returned);
        Assert.Equal(PendingStatus.Sent, store.Read("b-0").Pending!.Status);
        Assert.Equal(PendingStatus.Sent, store.Read("b-2").Pending!.Status);
    }

    [Fact]
    public async Task REQ_BT_020_A_failure_the_row_did_not_record_leaves_the_pack_to_its_lock()
    {
        bus.EchoSends = false;
        worker.Handler = (_, _) => Task.FromResult(WorkerResult.Contended);
        store.LoseEveryWrite = true;
        Queue(1);
        Run();
        leadership.Gain();

        await Until(() => { lock (errors) return errors.Contains("deliver"); }, "logged");
        await Task.Delay(50);

        Assert.Null(bus.Received.Single().Settled);
    }

    [Fact]
    public async Task REQ_BT_021_With_an_outage_on_the_rest_of_the_pack_posts_nothing_and_goes_back()
    {
        var posts = 0;
        options = options with { DeliveryConcurrency = 1 };
        bus.EchoSends = false;
        worker.Ready = false;
        worker.Handler = (_, _) =>
        {
            Interlocked.Increment(ref posts);
            return Task.FromResult(WorkerResult.Unavailable);
        };
        Queue(3);
        Run();
        leadership.Gain();

        await Until(() => Completed == 3, "the pack settled");

        Assert.Equal(1, posts);
        Assert.All(new[] { "b-0", "b-1", "b-2" }, id => Assert.Equal(PendingStatus.Pending, store.Read(id).Pending!.Status));
    }

    [Fact]
    public async Task REQ_BT_009_With_40_slots_the_41st_event_starts_only_after_a_release()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        bus.EchoSends = false;
        worker.Handler = async (_, _) =>
        {
            Interlocked.Increment(ref started);
            await release.Task;
            return WorkerResult.Done;
        };
        Queue(100);
        Run();
        leadership.Gain();

        await Until(() => started == 40, "the slots filled");
        await Task.Delay(50);
        Assert.Equal(40, started);

        release.SetResult();
        await Until(() => Completed == 100, "the pack delivered");
    }

    [Fact]
    public async Task REQ_BT_009_The_next_pack_waits_until_every_event_of_this_one_holds_a_slot()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        options = options with { DeliveryConcurrency = 1 };
        bus.PackSize = 3;
        worker.Handler = async (_, _) =>
        {
            await release.Task;
            return WorkerResult.Done;
        };
        Queue(6);
        Run();
        leadership.Gain();

        await Until(() => bus.Receives >= 1, "the first pack");
        await Task.Delay(50);
        Assert.Equal(1, bus.Receives);

        release.SetResult();
        await Until(() => Completed == 6, "both packs delivered");
    }

    [Fact]
    public async Task REQ_BT_021_A_message_received_as_an_outage_starts_goes_back_unposted()
    {
        var posted = new ConcurrentQueue<string>();
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Ready = false;
        worker.Handler = async (message, _) =>
        {
            posted.Enqueue(message.BatchId);
            await fail.Task;
            return WorkerResult.Unavailable;
        };
        bus.WhileReceiving = async taken =>
        {
            if (taken.All(d => d.Event.BatchId != "late")) return;
            fail.SetResult();
            await Until(() => store.Read("b-0").Pending!.Status == PendingStatus.Pending, "outage on");
        };
        Queue(1);
        Run();
        leadership.Gain();
        await Until(() => posted.Count == 1, "first delivery in flight");

        QueueOne("late");

        await Until(() => store.Read("late").Pending!.Status == PendingStatus.Pending, "late event back");
        Assert.DoesNotContain("late", posted);
    }

    [Fact]
    public async Task REQ_BT_019_A_term_closes_the_connection_only_after_its_deliveries_end()
    {
        var openWhenCancelled = -1;
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Handler = async (_, token) =>
        {
            inFlight.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(30, CancellationToken.None);
                openWhenCancelled = bus.OpenConnections;
                throw;
            }
            return WorkerResult.Done;
        };
        bus.Incoming.Enqueue(new FakeDelivery(Event("b-1")));
        Run();
        leadership.Gain();
        await inFlight.Task.WaitAsync(Limit);

        leadership.Lose();
        await Until(() => bus.OpenConnections == 0, "connection closed");

        Assert.Equal(1, openWhenCancelled);
    }

    [Fact]
    public async Task REQ_BT_009_A_delivery_that_throws_is_logged_and_the_others_go_on()
    {
        worker.Handler = (message, _) => message.BatchId == "b-0"
            ? throw new InvalidOperationException("broken")
            : Task.FromResult(WorkerResult.Done);
        Queue(3);
        Run();
        leadership.Gain();

        await Until(() => Completed == 2, "the others delivered");

        await Until(() => { lock (errors) return errors.Contains("receive"); }, "logged");
    }

    [Fact]
    public async Task REQ_BT_009_An_idle_sweep_waits_longer_and_a_moved_row_brings_it_back()
    {
        options = options with { SweepInterval = TimeSpan.FromMilliseconds(10), MaxIdleBackoff = 16 };
        Run();
        leadership.Gain();

        // At a steady 10 ms this is near 100 reads; doubling to 160 ms makes it under 20.
        await Task.Delay(1000);
        Assert.InRange(store.DueReads, 2, 20);

        AddPending();
        await Until(() => bus.Sent.Count == 1, "row sent");
        var after = store.DueReads;
        await Task.Delay(200);
        Assert.True(store.DueReads - after >= 3, $"{store.DueReads - after} reads after a moved row; stuck at 160 ms it is 1");
    }

    [Fact]
    public void REQ_BT_009_An_idle_sweep_never_waits_past_its_cap()
    {
        var pace = new SweepPace(new DispatcherOptions());

        for (var i = 0; i < 20; i++) pace.After(0);

        Assert.Equal(TimeSpan.FromSeconds(4), pace.After(0));
        Assert.Equal(TimeSpan.FromMilliseconds(200), pace.After(1));
    }
}
