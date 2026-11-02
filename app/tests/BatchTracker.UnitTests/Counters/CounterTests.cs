using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.UnitTests;

public class CounterTests
{
    private const string Farm = "farm-a";

    private static readonly Caller Ada = DemoUsers.Find("ada")!;

    private readonly FakeStore store = new();
    private readonly FakeHops hops = new();
    private readonly FakeCounters counters = new();
    private readonly Clock clock = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));

    private BatchService Service() => new(store, hops, clock, counters);

    private string[] Names() => counters.Counted.Select(c => c.Name).Order(StringComparer.Ordinal).ToArray();

    /// <summary>A batch at rest in a state, as the Worker leaves it.</summary>
    private Batch At(BatchState state)
    {
        var id = Guid.NewGuid().ToString();
        store.Put(new Batch { Id = id, FarmId = Farm, Product = "p", State = state, CreatedAt = clock.Now });
        return store.Read(id);
    }

    private Task<Outcome<Versioned>> Advance(Batch batch, BatchState to) =>
        Service().AdvanceAsync(Ada, Farm, batch.Id, to.ToString(), store.ETag(batch.Id), default);

    [Fact]
    public async Task REQ_BT_046_Create_counts_a_batch_and_an_event_once()
    {
        await Service().CreateAsync(Ada, Farm, "p", null, default);

        Assert.Equal([MetricNames.BatchesCreated, MetricNames.EventsCreated], Names());
        Assert.All(counters.Counted, c => Assert.Equal(Farm, c.Farm));
    }

    [Fact]
    public async Task REQ_BT_046_Repeated_create_with_the_same_key_adds_nothing()
    {
        await Service().CreateAsync(Ada, Farm, "p", "key", default);
        counters.Counted.Clear();

        await Service().CreateAsync(Ada, Farm, "p", "key", default);

        Assert.Empty(counters.Counted);
    }

    [Fact]
    public async Task REQ_BT_046_Refused_create_adds_nothing()
    {
        await Service().CreateAsync(Ada, Farm, "", null, default);

        Assert.Empty(counters.Counted);
    }

    [Theory]
    [InlineData(BatchState.Created, BatchState.Dose1, MetricNames.Dose1)]
    [InlineData(BatchState.Dose1, BatchState.Dose2, MetricNames.Dose2)]
    [InlineData(BatchState.Inspected, BatchState.Shipped, MetricNames.BatchesShipped)]
    [InlineData(BatchState.Shipped, BatchState.Closed, MetricNames.BatchesClosed)]
    public async Task REQ_BT_046_Each_move_counts_itself_and_an_event(BatchState from, BatchState to, string name)
    {
        await Advance(At(from), to);

        Assert.Equal(new[] { name, MetricNames.EventsCreated }.Order(StringComparer.Ordinal), Names());
    }

    [Fact]
    public async Task REQ_BT_046_Passed_inspection_counts_as_passed()
    {
        var batch = At(BatchState.Dose2);

        await Service().InspectAsync(Ada, Farm, batch.Id, true, null, store.ETag(batch.Id), default);

        Assert.Equal(new[] { MetricNames.InspectionsPassed, MetricNames.EventsCreated }.Order(StringComparer.Ordinal), Names());
    }

    [Fact]
    public async Task REQ_BT_046_Failed_inspection_counts_as_failed_and_rejected()
    {
        var batch = At(BatchState.Dose2);

        await Service().InspectAsync(Ada, Farm, batch.Id, false, "bad", store.ETag(batch.Id), default);

        Assert.Equal(
            new[] { MetricNames.InspectionsFailed, MetricNames.BatchesRejected, MetricNames.EventsCreated }.Order(StringComparer.Ordinal),
            Names());
    }

    [Fact]
    public async Task REQ_BT_046_Stale_etag_adds_nothing()
    {
        var batch = At(BatchState.Created);

        await Service().AdvanceAsync(Ada, Farm, batch.Id, "Dose1", "\"old\"", default);

        Assert.Empty(counters.Counted);
    }

    [Fact]
    public async Task REQ_BT_046_A_412_race_inside_the_write_adds_nothing()
    {
        var batch = At(BatchState.Created);
        store.BeforeReplace = _ =>
        {
            store.Put(batch);
            return Task.CompletedTask;
        };

        await Advance(batch, BatchState.Dose1);

        Assert.Equal(1, store.LostWrites);
        Assert.Empty(counters.Counted);
    }

    [Fact]
    public async Task REQ_BT_046_Illegal_move_adds_nothing()
    {
        await Advance(At(BatchState.Created), BatchState.Closed);

        Assert.Empty(counters.Counted);
    }

    [Fact]
    public async Task REQ_BT_046_A_write_that_throws_adds_nothing()
    {
        var batch = At(BatchState.Created);
        store.BeforeReplace = _ => throw new InvalidOperationException("store down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Advance(batch, BatchState.Dose1));

        Assert.Empty(counters.Counted);
    }

    [Fact]
    public async Task REQ_BT_046_Replay_of_a_blocked_batch_counts_once()
    {
        var batch = store.Put(At(BatchState.Created) with
        {
            Pending = new PendingEvent { EventId = "e", TraceId = "t", Status = PendingStatus.Dead }
        }).Batch;

        await Service().ReplayAsync(Ada, Farm, batch.Id, default);

        Assert.Equal([MetricNames.EventsReplayed], Names());
    }

    [Fact]
    public async Task REQ_BT_046_Replay_refused_adds_nothing()
    {
        await Service().ReplayAsync(Ada, Farm, At(BatchState.Created).Id, default);

        Assert.Empty(counters.Counted);
    }

    private static BatchEvent EventOf(Batch batch) => new(batch.Pending!.EventId, batch.Id, batch.FarmId, "t", batch.State.ToString());

    private Batch Pending()
    {
        var batch = At(BatchState.Dose1);
        return store.Put(batch with
        {
            Steps = [new Step { State = BatchState.Dose1, At = clock.Now, By = "ada", TraceId = "t" }],
            Pending = new PendingEvent { EventId = "e-1", TraceId = "t", Status = PendingStatus.Sent }
        }).Batch;
    }

    private EventRecorder Recorder() => new(store, hops, clock, counters, new StoreClock(store, clock));

    [Fact]
    public async Task REQ_BT_046_Recorded_event_counts_as_passed_once()
    {
        await Recorder().HandleAsync(EventOf(Pending()), default);

        Assert.Equal([MetricNames.EventsPassed], Names());
    }

    [Fact]
    public async Task REQ_BT_046_Redelivery_adds_nothing()
    {
        var message = EventOf(Pending());
        await Recorder().HandleAsync(message, default);
        counters.Counted.Clear();

        await Recorder().HandleAsync(message, default);

        Assert.Empty(counters.Counted);
    }

    [Fact]
    public async Task REQ_BT_046_Event_refused_by_fail_next_adds_nothing()
    {
        var batch = store.Put(Pending() with { FailNext = true }).Batch;

        await Recorder().HandleAsync(EventOf(batch), default);

        Assert.Empty(counters.Counted);
    }

    [Fact]
    public async Task REQ_BT_046_Recorder_write_lost_twice_adds_nothing()
    {
        var batch = Pending();
        store.BeforeReplace = _ =>
        {
            store.BeforeReplace = _ =>
            {
                store.Put(batch);
                return Task.CompletedTask;
            };
            store.Put(batch);
            return Task.CompletedTask;
        };

        await Recorder().HandleAsync(EventOf(batch), default);

        Assert.Empty(counters.Counted);
    }

    private DeadLetters Letters() => new(store, hops, clock, counters);

    [Fact]
    public async Task REQ_BT_046_A_refused_event_counts_once()
    {
        var batch = Pending();

        await Letters().RefuseAsync(EventOf(batch), DispatcherOptions.RefusedText, default);

        Assert.Equal([MetricNames.EventsDeadLettered], Names());
    }

    [Fact]
    public async Task REQ_BT_046_An_event_given_back_adds_nothing()
    {
        await Letters().RequeueAsync(EventOf(Pending()), default);

        Assert.Empty(counters.Counted);
    }

    [Fact]
    public async Task REQ_BT_046_A_refusal_seen_again_adds_nothing()
    {
        var batch = Pending();
        await Letters().RefuseAsync(EventOf(batch), DispatcherOptions.RefusedText, default);
        counters.Counted.Clear();
        store.Put(store.Read(batch.Id) with { Pending = null });

        await Letters().RefuseAsync(EventOf(batch), DispatcherOptions.RefusedText, default);

        Assert.Empty(counters.Counted);
    }

    [Fact]
    public async Task REQ_BT_046_A_refusal_whose_write_lost_adds_nothing()
    {
        var batch = Pending();
        store.BeforeReplace = _ =>
        {
            store.Put(batch);
            return Task.CompletedTask;
        };

        await Letters().RefuseAsync(EventOf(batch), DispatcherOptions.RefusedText, default);

        Assert.Empty(counters.Counted);
    }
}
