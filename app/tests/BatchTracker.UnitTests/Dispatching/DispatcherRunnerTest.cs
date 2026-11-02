using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

/// <summary>What the tests of DispatcherRunnerTests and its neighbours share.</summary>
public abstract class DispatcherRunnerTest : IAsyncLifetime
{
    protected static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    protected readonly FakeStore store = new();
    protected readonly FakeHops hops = new();
    protected readonly FakeBus bus = new();
    protected readonly FakeWorker worker = new();
    protected readonly LeadershipSignal leadership = new();
    protected readonly DispatcherStatus status = new();
    protected readonly Clock clock = new(Start);
    protected readonly CancellationTokenSource stop = new();
    protected readonly List<string> errors = [];
    protected Task? running;
    protected DeadLetters? letters;

    protected DispatcherOptions options = new()
    {
        SweepInterval = TimeSpan.FromMilliseconds(5),
        WorkerProbeInterval = TimeSpan.FromMilliseconds(30),
        ReceiveWait = TimeSpan.FromMilliseconds(5),
        ErrorDelay = TimeSpan.FromMilliseconds(5),
        WorkerFailuresToStop = 1
    };

    protected void Run()
    {
        var runner = new DispatcherRunner(
            leadership, bus, worker,
            new Outbox(store, hops, clock, options),
            letters ?? new DeadLetters(store, hops, clock, new FakeCounters()),
            status, hops, clock, options,
            (_, step) => { lock (errors) errors.Add(step); });
        running = runner.RunAsync(stop.Token);
    }

    protected static async Task Until(Func<bool> condition, string what)
    {
        using var limit = new CancellationTokenSource(Limit);
        while (!condition())
        {
            if (limit.IsCancellationRequested) throw new TimeoutException(what);
            await Task.Delay(2);
        }
    }

    protected void AddPending(string id = "b-1") => store.Put(In(BatchState.Created, id) with
    {
        Pending = new PendingEvent { EventId = $"{id}:Created", TraceId = "t", Status = PendingStatus.Pending }
    });

    /// <summary>An event on the bus, and its row marked sent the way the sweep leaves it.</summary>
    protected void QueueOne(string id)
    {
        store.Put(In(BatchState.Created, id) with
        {
            Pending = new PendingEvent { EventId = $"{id}:Created", TraceId = "t", Status = PendingStatus.Sent, SentAt = Start }
        });
        bus.Incoming.Enqueue(new FakeDelivery(Event(id)));
    }

    protected void Queue(int count)
    {
        for (var i = 0; i < count; i++) QueueOne($"b-{i}");
    }

    protected static BatchEvent Event(string id) => new($"{id}:Created", id, "farm-a", "t", "Created");

    protected int Completed => bus.Received.Count(d => d.Settled == "complete");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await stop.CancelAsync();
        if (running is not null) await running.WaitAsync(Limit);
        stop.Dispose();
    }

}
