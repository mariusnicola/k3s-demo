using System.Collections.Concurrent;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using BatchTracker.Line;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

public class CorrelationFlowTests
{
    private const string EventId = "b-1:Created";

    /// <summary>Keeps the id that was current when each hop was reported, which is what goes on the wire.</summary>
    private sealed class WatchingHops : IHopReporter
    {
        public ConcurrentQueue<string?> Seen { get; } = new();
        public void Report(Hop hop) => Seen.Enqueue(Correlation.Current);
    }

    private readonly FakeStore store = new();
    private readonly WatchingHops hops = new();
    private readonly FakeBus bus = new() { EchoSends = false };
    private readonly Clock clock = new(Start);

    private Batch Pending(string status, string? id) => In(BatchState.Created) with
    {
        Pending = new PendingEvent { EventId = EventId, TraceId = "t", Status = status, CorrelationId = id }
    };

    private async Task SweepAsync()
    {
        await using var queue = await bus.ConnectAsync(default);
        await new Outbox(store, hops, clock, new DispatcherOptions()).SweepAsync(queue, default);
    }

    [Fact]
    public async Task REQ_BT_052_The_sweep_sends_the_id_of_the_event()
    {
        store.Put(Pending(PendingStatus.Pending, "linked"));

        await SweepAsync();

        Assert.True(bus.Sent.TryDequeue(out var sent));
        Assert.Equal("linked", sent.CorrelationId);
        Assert.All(hops.Seen, id => Assert.Equal("linked", id));
        Assert.NotEmpty(hops.Seen);
    }

    [Fact]
    public async Task REQ_BT_052_An_event_from_before_ids_gets_one_of_its_own()
    {
        store.Put(Pending(PendingStatus.Pending, null));

        await SweepAsync();

        Assert.True(bus.Sent.TryDequeue(out var sent));
        Assert.True(Correlation.IsValid(sent.CorrelationId));
    }

    [Fact]
    public async Task REQ_BT_052_The_sweep_leaves_no_id_behind()
    {
        store.Put(Pending(PendingStatus.Pending, "linked"));

        await SweepAsync();

        Assert.Null(Correlation.Current);
    }

    [Fact]
    public async Task REQ_BT_052_A_dead_letter_reuses_the_id_of_its_event()
    {
        store.Put(Pending(PendingStatus.Sent, "linked"));
        var message = new BatchEvent(EventId, "b-1", "farm-a", "t", "Created", "linked");

        await new DeadLetters(store, hops, clock, new FakeCounters()).HandleAsync(new FakeDelivery(message), default);

        Assert.Equal("linked", Assert.Single(hops.Seen));
    }

    private static LineEntry Entry(string? id) => new(
        Start, LineLevel.Info, LineReason.Started, "started", 1, null, "farm-a", LineStep.Create, 0, null, null, null, null, id);

    [Theory]
    [InlineData("abc-1", 1)]
    [InlineData("  abc-1  ", 1)]
    [InlineData("abc", 0)]
    [InlineData("ABC-1", 0)]
    public void REQ_BT_052_The_log_filter_is_exact_after_trimming(string filter, int matched)
    {
        var log = new LineLog();
        log.Add(Entry("abc-1"));
        log.Add(Entry("other"));

        Assert.Equal(matched, log.Query(new LogQuery(CorrelationId: filter)).Matched);
    }

    [Fact]
    public async Task REQ_BT_052_A_delivery_calls_the_Worker_with_the_id_of_its_event()
    {
        var seen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new FakeWorker
        {
            Handler = (_, _) =>
            {
                seen.TrySetResult(Correlation.Current);
                return Task.FromResult(WorkerResult.Done);
            }
        };
        var leadership = new LeadershipSignal();
        bus.Incoming.Enqueue(new FakeDelivery(new BatchEvent(EventId, "b-1", "farm-a", "t", "Created", "linked")));
        using var stop = new CancellationTokenSource();
        var running = Runner(leadership, worker, (_, _) => { }).RunAsync(stop.Token);
        leadership.Gain();

        Assert.Equal("linked", await seen.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        await stop.CancelAsync();
        await running;
    }

    [Fact]
    public async Task REQ_BT_052_A_failed_loop_turn_logs_with_an_id_of_its_own()
    {
        var logged = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeReplace = _ => throw new InvalidOperationException("store down");
        store.Put(Pending(PendingStatus.Pending, "linked"));
        var leadership = new LeadershipSignal();
        using var stop = new CancellationTokenSource();
        var running = Runner(leadership, new FakeWorker(), (_, step) =>
        {
            if (step == "sweep") logged.TrySetResult(Correlation.Current);
        }).RunAsync(stop.Token);
        leadership.Gain();

        Assert.True(Correlation.IsValid(await logged.Task.WaitAsync(TimeSpan.FromSeconds(10))));

        await stop.CancelAsync();
        await running;
    }

    /// <summary>A runner over the fakes, with short waits so a turn comes round at once.</summary>
    private DispatcherRunner Runner(ILeadership leadership, IWorkerClient worker, Action<Exception, string> logError)
    {
        var options = new DispatcherOptions { SweepInterval = TimeSpan.FromMilliseconds(10), ReceiveWait = TimeSpan.FromMilliseconds(10), ErrorDelay = TimeSpan.FromMilliseconds(10) };
        return new DispatcherRunner(
            leadership, bus, worker,
            new Outbox(store, hops, clock, options),
            new DeadLetters(store, hops, clock, new FakeCounters()),
            new DispatcherStatus(), hops, clock, options, logError);
    }

    [Fact]
    public async Task REQ_BT_055_A_replay_takes_the_new_id_and_records_the_one_that_died()
    {
        var id = Guid.NewGuid().ToString();
        store.Put(Pending(PendingStatus.Dead, "died") with { Id = id });
        var service = new BatchService(store, new FakeHops(), clock, new FakeCounters());
        using var action = Correlation.Use("replay");

        var outcome = await service.ReplayAsync(DemoUsers.Find("ada")!, "farm-a", id, default);

        Assert.True(outcome.Value is not null, outcome.Detail);
        var pending = store.Read(id).Pending!;
        Assert.Equal("replay", pending.CorrelationId);
        Assert.Equal("died", pending.ReplayOf);
    }
}
