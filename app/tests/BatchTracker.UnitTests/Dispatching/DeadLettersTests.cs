using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

/// <summary>A pack cannot dead-letter one event, so what happens to a failed event is written on its row.</summary>
public class DeadLettersTests
{
    private const string EventId = "b-1:Created";
    private const string Why = "Worker refused the event.";
    private static readonly BatchEvent Message = new(EventId, "b-1", "farm-a", "t", "Created");

    private readonly FakeStore store = new();
    private readonly FakeHops hops = new();
    private readonly FakeCounters counters = new();

    private DeadLetters NewHandler() => new(store, hops, new Clock(Start), counters);

    private Batch Sent() => In(BatchState.Created) with
    {
        Pending = new PendingEvent { EventId = EventId, TraceId = "t", Status = PendingStatus.Sent, SentAt = Start }
    };

    [Fact]
    public async Task REQ_BT_011_A_refused_event_blocks_the_batch()
    {
        store.Put(Sent());

        Assert.True(await NewHandler().RefuseAsync(Message, Why, default));

        var batch = store.Read("b-1");
        Assert.True(batch.IsBlocked());
        Assert.Equal(Why, batch.Pending!.Error);
        Assert.Null(batch.Pending.SentAt);
    }

    [Fact]
    public async Task REQ_BT_021_An_event_given_back_returns_to_pending()
    {
        store.Put(Sent());

        Assert.True(await NewHandler().RequeueAsync(Message, default));

        var pending = store.Read("b-1").Pending!;
        Assert.Equal(PendingStatus.Pending, pending.Status);
        Assert.Null(pending.SentAt);
    }

    [Fact]
    public async Task REQ_BT_021_A_letter_goes_back_to_pending_and_completes()
    {
        store.Put(Sent());
        var letter = new FakeDelivery(Message);

        await NewHandler().HandleAsync(letter, default);

        Assert.Equal(PendingStatus.Pending, store.Read("b-1").Pending!.Status);
        Assert.Equal("complete", letter.Settled);
    }

    [Fact]
    public async Task REQ_BT_021_A_letter_whose_write_lost_is_not_completed()
    {
        store.Put(Sent());
        store.LoseEveryWrite = true;
        var letter = new FakeDelivery(Message);

        await NewHandler().HandleAsync(letter, default);

        Assert.Null(letter.Settled);
    }

    [Fact]
    public async Task REQ_BT_021_A_letter_holding_three_sent_rows_returns_them_all_and_completes_once()
    {
        var dead = new FakeReceiver();
        await using var queue = new ServiceBusEventQueue(new FakeBusClient(new FakeSender(), new FakeReceiver(), dead), "batch-events");
        foreach (var id in new[] { "x", "y", "z" }) store.Put(In(BatchState.Created, id) with { Pending = new PendingEvent { EventId = $"{id}:Created", TraceId = "t", Status = PendingStatus.Sent } });
        dead.Waiting.Enqueue(FakeReceiver.Pack("x", "y", "z"));

        var letters = await queue.ReceiveDeadLettersAsync(1, TimeSpan.FromMilliseconds(20), default);
        foreach (var letter in letters) await NewHandler().HandleAsync(letter, default);

        Assert.Equal(3, letters.Count);
        Assert.All(new[] { "x", "y", "z" }, id => Assert.Equal(PendingStatus.Pending, store.Read(id).Pending!.Status));
        Assert.Single(dead.Completed);
    }

    [Fact]
    public async Task REQ_BT_011_A_second_refusal_changes_nothing_and_counts_once()
    {
        store.Put(Sent());
        var handler = NewHandler();
        await handler.RefuseAsync(Message, Why, default);
        var etag = store.ETag("b-1");

        Assert.True(await handler.RefuseAsync(Message, Why, default));

        Assert.Equal(etag, store.ETag("b-1"));
        Assert.Single(counters.Counted, c => c.Name == MetricNames.EventsDeadLettered);
    }

    [Fact]
    public async Task REQ_BT_011_A_letter_for_a_dead_row_leaves_it_dead()
    {
        store.Put(Sent());
        var handler = NewHandler();
        await handler.RefuseAsync(Message, Why, default);
        var etag = store.ETag("b-1");
        var letter = new FakeDelivery(Message);

        await handler.HandleAsync(letter, default);

        Assert.Equal(etag, store.ETag("b-1"));
        Assert.True(store.Read("b-1").IsBlocked());
        Assert.Equal("complete", letter.Settled);
    }

    [Fact]
    public async Task REQ_BT_021_A_row_already_pending_stays_as_it_is()
    {
        store.Put(Sent() with { Pending = Sent().Pending! with { Status = PendingStatus.Pending, SentAt = null } });
        var etag = store.ETag("b-1");

        Assert.True(await NewHandler().RequeueAsync(Message, default));

        Assert.Equal(etag, store.ETag("b-1"));
    }

    [Fact]
    public async Task REQ_BT_010_A_finished_event_changes_nothing()
    {
        store.Put(In(BatchState.Created));
        var etag = store.ETag("b-1");

        Assert.True(await NewHandler().RefuseAsync(Message, Why, default));
        Assert.True(await NewHandler().RequeueAsync(Message, default));

        Assert.Equal(etag, store.ETag("b-1"));
    }

    [Fact]
    public async Task REQ_BT_010_An_older_event_changes_nothing()
    {
        store.Put(In(BatchState.Dose1) with
        {
            Pending = new PendingEvent { EventId = "b-1:Dose1", TraceId = "t", Status = PendingStatus.Sent }
        });

        Assert.True(await NewHandler().RefuseAsync(Message, Why, default));

        Assert.False(store.Read("b-1").IsBlocked());
    }

    [Fact]
    public async Task REQ_BT_010_A_batch_that_is_gone_is_settled()
    {
        Assert.True(await NewHandler().RefuseAsync(Message, Why, default));
        Assert.True(await NewHandler().RequeueAsync(Message, default));
    }

    [Fact]
    public async Task REQ_BT_011_A_lost_write_says_so_and_counts_nothing()
    {
        store.Put(Sent());
        store.LoseEveryWrite = true;

        Assert.False(await NewHandler().RefuseAsync(Message, Why, default));
        Assert.False(await NewHandler().RequeueAsync(Message, default));

        Assert.False(store.Read("b-1").IsBlocked());
        Assert.Empty(counters.Counted);
    }

    [Fact]
    public async Task REQ_BT_028_A_refused_event_reports_a_failed_hop()
    {
        store.Put(Sent());

        await NewHandler().RefuseAsync(Message, Why, default);

        var hop = Assert.Single(hops.Hops);
        Assert.Equal(HopNames.Dispatcher, hop.Name);
        Assert.False(hop.Ok);
    }
}
