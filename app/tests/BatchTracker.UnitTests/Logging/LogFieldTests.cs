using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using Microsoft.Extensions.Logging;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

/// <summary>A log line written during work on a batch names it, so the filters on /live/logs find the line.</summary>
public class LogFieldTests
{
    private sealed class Sink : ILogSink
    {
        public List<ServiceLogEntry> Entries { get; } = [];
        public void Add(ServiceLogEntry entry) => Entries.Add(entry);
    }

    private static readonly BatchEvent Message = new("b-1:Dose1", "b-1", "farm-a", "t", "Dose1");

    private readonly FakeStore store = new();
    private readonly FakeBus bus = new() { EchoSends = false };
    private readonly Clock clock = new(Start);

    /// <summary>What the fields read while the store is being written to.</summary>
    private (string? Farm, string? Batch, string? Step) seen;

    private void WatchTheWrite() => store.BeforeReplace = _ =>
    {
        seen = (Correlation.Farm, Correlation.BatchId, Correlation.Step);
        return Task.CompletedTask;
    };

    private Batch Awaiting(string id = "b-1") => In(BatchState.Created, id) with
    {
        Pending = new PendingEvent { EventId = $"{id}:Dose1", TraceId = "t", Status = PendingStatus.Pending }
    };

    [Fact]
    public void REQ_BT_045_A_log_line_about_a_batch_fills_the_entry()
    {
        var sink = new Sink();
        var logger = new ServiceLogProvider(() => sink, "api", "api-1").CreateLogger("BatchTracker.Test");
        using var action = Correlation.Use("id-1");
        Correlation.About("farm-a", "b-1", "Dose1");

        logger.LogError("It failed");

        var entry = Assert.Single(sink.Entries);
        Assert.Equal("farm-a", entry.Farm);
        Assert.Equal("b-1", entry.BatchId);
        Assert.Equal("Dose1", entry.Step);
    }

    [Fact]
    public void REQ_BT_045_A_log_line_about_no_batch_leaves_them_empty()
    {
        var sink = new Sink();
        var logger = new ServiceLogProvider(() => sink, "api", "api-1").CreateLogger("BatchTracker.Test");
        using var action = Correlation.Use("id-1");

        logger.LogError("It failed");

        var entry = Assert.Single(sink.Entries);
        Assert.Null(entry.Farm);
        Assert.Null(entry.BatchId);
        Assert.Null(entry.Step);
    }

    [Fact]
    public void REQ_BT_045_The_fields_end_with_their_action()
    {
        using (Correlation.Use("id-1")) Correlation.About("farm-a", "b-1", "Dose1");

        Assert.Null(Correlation.Farm);
    }

    /// <summary>Notes what the log context names as each hop goes.</summary>
    private sealed class NamingHops : IHopReporter
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, string?> Named { get; } = new();
        public void Report(Hop hop) => Named[hop.BatchId] = Correlation.BatchId;
    }

    [Fact]
    public async Task REQ_BT_045_Each_hop_of_a_send_names_its_own_batch()
    {
        for (var i = 0; i < 8; i++) store.Put(Awaiting($"b-{i}"));
        var hops = new NamingHops();
        await using var queue = await bus.ConnectAsync(default);

        await new Outbox(store, hops, clock, new DispatcherOptions()).SweepAsync(queue, default);

        Assert.Equal(8, hops.Named.Count);
        Assert.All(hops.Named, n => Assert.Equal(n.Key, n.Value));
    }

    [Fact]
    public async Task REQ_BT_045_A_failed_send_leaves_the_round_naming_its_batch()
    {
        store.Put(Awaiting());
        bus.SendFails = true;
        await using var queue = await bus.ConnectAsync(default);
        using var round = Correlation.Use("round");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new Outbox(store, new FakeHops(), clock, new DispatcherOptions()).SweepAsync(queue, default));

        Assert.Equal("b-1", Correlation.BatchId);
        Assert.Equal("farm-a", Correlation.Farm);
    }

    [Fact]
    public async Task REQ_BT_045_The_worker_names_the_event_it_records()
    {
        store.Put(BatchMachine.Advance(In(BatchState.Created), BatchState.Dose1, StampAt(1)).Batch!);
        WatchTheWrite();
        using var action = Correlation.Use("id-1");

        await new EventRecorder(store, new FakeHops(), clock, new FakeCounters(), new StoreClock(store, clock)).HandleAsync(Message, default);

        Assert.Equal(("farm-a", "b-1", "Dose1"), seen);
    }

    [Fact]
    public async Task REQ_BT_045_A_dead_letter_names_its_batch()
    {
        store.Put(Awaiting());
        WatchTheWrite();
        using var action = Correlation.Use("id-1");
        Correlation.About(Message.FarmId, Message.BatchId, Message.State);

        await new DeadLetters(store, new FakeHops(), clock, new FakeCounters()).RefuseAsync(Message, DispatcherOptions.RefusedText, default);

        Assert.Equal(("farm-a", "b-1", "Dose1"), seen);
    }

    [Fact]
    public async Task REQ_BT_045_A_create_names_its_farm_and_batch()
    {
        using var action = Correlation.Use("id-1");
        var service = new BatchService(store, new FakeHops(), clock, new FakeCounters());

        var outcome = await service.CreateAsync(DemoUsers.Find("ada")!, "farm-a", "Vaccine", null, default);

        Assert.True(outcome.Value is not null, outcome.Detail);
        Assert.Equal(("farm-a", outcome.Value.Batch.Id, "Created"), (Correlation.Farm, Correlation.BatchId, Correlation.Step));
    }

    [Fact]
    public async Task REQ_BT_045_A_create_refused_for_its_farm_names_no_batch()
    {
        using var action = Correlation.Use("id-1");
        var service = new BatchService(store, new FakeHops(), clock, new FakeCounters());

        await service.CreateAsync(DemoUsers.Find("viewer")!, "farm-z", "Vaccine", null, default);

        Assert.Null(Correlation.BatchId);
    }

    [Fact]
    public async Task REQ_BT_045_A_change_names_the_batch_and_the_step_it_enters()
    {
        var id = Guid.NewGuid().ToString();
        store.Put(In(BatchState.Created, id));
        WatchTheWrite();
        using var action = Correlation.Use("id-1");
        var service = new BatchService(store, new FakeHops(), clock, new FakeCounters());

        var outcome = await service.AdvanceAsync(DemoUsers.Find("ada")!, "farm-a", id, nameof(BatchState.Dose1), store.ETag(id), default);

        Assert.True(outcome.Value is not null, outcome.Detail);
        Assert.Equal(("farm-a", id, "Dose1"), seen);
    }
}
