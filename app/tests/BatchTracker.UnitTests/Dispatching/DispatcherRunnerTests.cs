using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

public sealed class DispatcherRunnerTests : DispatcherRunnerTest
{
    [Fact]
    public async Task REQ_BT_018_Without_the_lease_nothing_connects_or_sends()
    {
        AddPending();
        Run();

        await Task.Delay(50);

        Assert.Equal(0, bus.Connects);
        Assert.Empty(bus.Sent);
        Assert.False(status.Leader);
        Assert.False(status.Connected);
    }

    [Fact]
    public async Task REQ_BT_009_Holder_sends_delivers_and_completes()
    {
        AddPending();
        Run();
        leadership.Gain();

        await Until(() => bus.Received.Any(d => d.Settled == "complete"), "complete");

        Assert.True(status.Leader);
        Assert.True(status.Connected);
        Assert.Single(bus.Sent);
    }

    [Fact]
    public async Task REQ_BT_011_Worker_422_blocks_the_row_at_once_and_settles_the_event()
    {
        worker.Handler = (_, _) => Task.FromResult(WorkerResult.Refused);
        AddPending();
        Run();
        leadership.Gain();

        await Until(() => bus.Received.Any(d => d.Settled == "complete"), "settled");

        Assert.True(store.Read("b-1").IsBlocked());
        Assert.Equal(DispatcherOptions.RefusedText, store.Read("b-1").Pending!.Error);
    }

    [Fact]
    public async Task REQ_BT_021_Worker_503_gives_the_event_back_and_stops_receiving_and_sweeping_until_ready()
    {
        var calls = 0;
        worker.Ready = false;
        worker.Handler = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(WorkerResult.Unavailable);
        };
        AddPending("b-1");
        Run();
        leadership.Gain();
        await Until(() => store.Returned > 0, "given back");
        await Until(() => worker.ReadyCalls >= 2, "ready probes");

        // Added while the Worker is down: neither loop may touch it.
        AddPending("b-2");
        bus.Incoming.Enqueue(new FakeDelivery(new BatchEvent("x:Created", "x", "farm-a", "t", "Created")));
        await Task.Delay(100);

        Assert.Equal(1, calls);
        Assert.Equal(PendingStatus.Pending, store.Read("b-2").Pending!.Status);
        Assert.False(store.Read("b-1").IsBlocked());

        worker.Handler = (_, _) => Task.FromResult(WorkerResult.Done);
        worker.Ready = true;

        await Until(() => store.Read("b-2").Pending!.Status == PendingStatus.Sent, "sweep resumed");
        await Until(() => bus.Received.Count(d => d.Settled == "complete") >= 3, "receive resumed");
    }

    [Fact]
    public async Task REQ_BT_009_A_backlog_waits_a_full_interval_between_rounds()
    {
        options = options with { SweepInterval = TimeSpan.FromHours(1), SweepSize = 50 };
        for (var i = 0; i < 120; i++) AddPending($"b-{i}");
        Run();
        leadership.Gain();

        await Until(() => bus.Sent.Count == 50, "the first round");
        await Task.Delay(100);

        Assert.Equal(50, bus.Sent.Count);
        Assert.Equal(1, bus.BatchSends);
    }

    [Fact]
    public async Task REQ_BT_021_A_Worker_outage_stops_the_next_round()
    {
        options = options with { SweepSize = 10 };
        worker.Ready = false;
        worker.Handler = (_, _) => Task.FromResult(WorkerResult.Unavailable);
        bus.SendTime = TimeSpan.FromMilliseconds(20);
        for (var i = 0; i < 400; i++) AddPending($"b-{i}");
        Run();
        leadership.Gain();
        await Until(() => store.Returned > 0, "given back");
        await Until(() => bus.Sent.Count > 0, "first sends");

        // Long enough for all 400 to go if the outage were ignored; a round already running may finish.
        await Task.Delay(1500);

        Assert.True(bus.Sent.Count < 150, $"{bus.Sent.Count} sent");
    }

    [Fact]
    public async Task REQ_BT_021_Good_event_refused_by_nothing_ends_complete_after_the_outage()
    {
        worker.Handler = (_, _) => Task.FromResult(WorkerResult.Unavailable);
        worker.Ready = false;
        AddPending();
        Run();
        leadership.Gain();
        await Until(() => worker.ReadyCalls >= 1, "outage noticed");

        // The row went back to pending, so the sweep sends it again once the Worker answers.
        worker.Handler = (_, _) => Task.FromResult(WorkerResult.Done);
        worker.Ready = true;

        await Until(() => bus.Sent.Count == 2, "sent again");
        await Until(() => bus.Received.Count(d => d.Settled == "complete") == 2, "complete");
        Assert.False(store.Read("b-1").IsBlocked());
    }

    [Fact]
    public async Task REQ_BT_018_Bus_down_at_start_reports_leader_not_connected_then_recovers()
    {
        bus.Down = true;
        AddPending();
        Run();
        leadership.Gain();

        await Until(() => bus.Connects >= 3, "retries");
        Assert.True(status.Leader);
        Assert.False(status.Connected);
        Assert.Empty(bus.Sent);

        bus.Down = false;

        await Until(() => status.Connected && !bus.Sent.IsEmpty, "recovered");
    }

    [Fact]
    public async Task REQ_BT_020_Bus_lost_mid_term_reports_not_connected_and_recovers()
    {
        Run();
        leadership.Gain();
        await Until(() => status.Connected, "connected");

        bus.Down = true;
        await Until(() => !status.Connected, "not connected");
        Assert.True(status.Leader);

        bus.Down = false;
        await Until(() => status.Connected, "reconnected");
    }

    [Fact]
    public async Task REQ_BT_019_Lost_lease_mid_delivery_closes_the_connection_and_leaves_the_message_unsettled()
    {
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Handler = async (_, token) =>
        {
            inFlight.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return WorkerResult.Done;
        };
        AddPending();
        Run();
        leadership.Gain();
        await inFlight.Task.WaitAsync(Limit);

        leadership.Lose();

        Assert.False(status.Connected);
        await Until(() => bus.OpenConnections == 0 && !status.Leader, "connection closed");
        Assert.Null(bus.Received.Single().Settled);
        Assert.Equal(0, store.Returned);
        Assert.Equal(PendingStatus.Sent, store.Read("b-1").Pending!.Status);
    }

    [Fact]
    public async Task REQ_BT_019_New_term_redelivers_what_the_lost_term_left_unsettled()
    {
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Handler = async (_, token) =>
        {
            inFlight.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return WorkerResult.Done;
        };
        AddPending();
        Run();
        leadership.Gain();
        await inFlight.Task.WaitAsync(Limit);
        leadership.Lose();
        await Until(() => bus.OpenConnections == 0, "connection closed");

        worker.Handler = (_, _) => Task.FromResult(WorkerResult.Done);
        bus.Incoming.Enqueue(new FakeDelivery(bus.Sent.Single()));
        leadership.Gain();

        await Until(() => bus.Received.Any(d => d.Settled == "complete"), "redelivered");
        Assert.Equal(2, bus.Connects);
    }

    [Fact]
    public async Task REQ_BT_021_Dead_letters_are_read_and_completed_by_the_holder()
    {
        bus.EchoSends = false;
        store.Put(In(BatchState.Created) with
        {
            Pending = new PendingEvent { EventId = "b-1:Created", TraceId = "t", Status = PendingStatus.Sent, SentAt = clock.Now }
        });
        var letter = new FakeDelivery(new BatchEvent("b-1:Created", "b-1", "farm-a", "t", "Created"));
        bus.Letters.Enqueue(letter);
        Run();
        leadership.Gain();

        await Until(() => letter.Settled == "complete", "letter completed");

        Assert.Equal(1, store.Returned);
    }

    [Fact]
    public async Task REQ_BT_011_One_refused_event_of_three_blocks_its_row_counts_once_and_the_pack_completes()
    {
        var counters = new FakeCounters();
        letters = new DeadLetters(store, hops, clock, counters);
        worker.Handler = (message, _) => Task.FromResult(message.BatchId == "b-1" ? WorkerResult.Refused : WorkerResult.Done);
        Queue(3);
        Run();
        leadership.Gain();

        await Until(() => Completed == 3, "pack settled");

        Assert.True(store.Read("b-1").IsBlocked());
        Assert.False(store.Read("b-0").IsBlocked());
        Assert.Single(counters.Counted, c => c.Name == MetricNames.EventsDeadLettered);
    }

    [Fact]
    public async Task REQ_BT_019_Stopping_the_host_ends_the_runner()
    {
        Run();
        leadership.Gain();
        await Until(() => status.Connected, "connected");

        await stop.CancelAsync();

        await running!.WaitAsync(Limit);
        Assert.Equal(0, bus.OpenConnections);
        Assert.False(status.Leader);
    }
}
