using BatchTracker.Application;
using BatchTracker.Domain;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

public class EventRecorderTests
{
    private static readonly BatchEvent Message = new("b-1:Dose1", "b-1", "farm-a", "t", "Dose1");

    private readonly FakeStore store = new();
    private readonly FakeHops hops = new();

    private EventRecorder NewRecorder() => new(store, hops, new Clock(Start), new FakeCounters(), new StoreClock(store, new Clock(Start)));

    private Batch Awaiting() => BatchMachine.Advance(In(BatchState.Created), BatchState.Dose1, StampAt(1)).Batch! with { };

    [Fact]
    public async Task REQ_BT_009_Event_fills_the_step_and_clears_pending()
    {
        store.Put(Awaiting());

        var result = await NewRecorder().HandleAsync(Message, default);

        var batch = store.Read("b-1");
        Assert.Equal(RecordResult.Done, result);
        Assert.Null(batch.Pending);
        Assert.NotEmpty(batch.Steps[^1].RecordId);
        Assert.True(HashChain.Verify(batch));
    }

    [Fact]
    public async Task REQ_BT_010_Redelivered_event_writes_nothing()
    {
        store.Put(Awaiting());
        await NewRecorder().HandleAsync(Message, default);
        var etag = store.ETag("b-1");

        var result = await NewRecorder().HandleAsync(Message, default);

        Assert.Equal(RecordResult.Done, result);
        Assert.Equal(etag, store.ETag("b-1"));
    }

    [Fact]
    public async Task REQ_BT_010_Event_for_an_older_step_writes_nothing()
    {
        store.Put(Awaiting());
        var etag = store.ETag("b-1");

        var result = await NewRecorder().HandleAsync(Message with { EventId = "b-1:Created" }, default);

        Assert.Equal(RecordResult.Done, result);
        Assert.Equal(etag, store.ETag("b-1"));
    }

    [Fact]
    public async Task REQ_BT_011_FailNext_refuses_and_writes_nothing()
    {
        store.Put(Awaiting() with { FailNext = true });
        var etag = store.ETag("b-1");

        var result = await NewRecorder().HandleAsync(Message, default);

        Assert.Equal(RecordResult.Refused, result);
        Assert.Equal(etag, store.ETag("b-1"));
    }

    [Fact]
    public async Task REQ_BT_009_Replace_race_rereads_once_and_keeps_what_the_racer_wrote()
    {
        store.Put(Awaiting());
        var sentAt = Start.AddMinutes(9);
        store.BeforeReplace = _ =>
        {
            var current = store.Read("b-1");
            store.Put(current with { Pending = current.Pending! with { Status = "sent", SentAt = sentAt } });
            return Task.CompletedTask;
        };

        var result = await NewRecorder().HandleAsync(Message, default);

        Assert.Equal(RecordResult.Done, result);
        Assert.Equal(1, store.LostWrites);
        Assert.Null(store.Read("b-1").Pending);
    }

    [Fact]
    public async Task REQ_BT_009_Second_lost_race_reports_contended_so_the_message_returns()
    {
        store.Put(Awaiting());
        void Race() => store.BeforeReplace = _ =>
        {
            store.Put(store.Read("b-1") with { Product = "changed" });
            if (store.LostWrites == 0) Race();
            return Task.CompletedTask;
        };
        Race();

        var result = await NewRecorder().HandleAsync(Message, default);

        Assert.Equal(RecordResult.Contended, result);
        Assert.NotNull(store.Read("b-1").Pending);
    }

    [Fact]
    public async Task REQ_BT_028_Worker_reports_worker_and_record_hops()
    {
        store.Put(Awaiting());

        await NewRecorder().HandleAsync(Message, default);

        Assert.Equal([HopNames.Worker, HopNames.Record], hops.Hops.Select(h => h.Name));
    }
}
