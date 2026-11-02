using BatchTracker.Application;
using BatchTracker.Domain;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

/// <summary>A request must not read every batch ever written: setup's drain waits on this route.</summary>
public sealed class SummaryTests
{
    private static readonly Caller FarmA = new("viewer", [Permissions.BatchRead], ["farm-a"]);
    private static readonly Caller Everyone = new("ada", Permissions.All, [Caller.AllFarms]);

    private readonly FakeStore store = new();

    private BatchService Service() => new(store, new FakeHops(), new Clock(Start), new FakeCounters());

    private static Batch Waiting(string id, string farm, string status) => In(BatchState.Created, id) with
    {
        FarmId = farm,
        Pending = new PendingEvent { EventId = $"{id}:Created", TraceId = "t", Status = status }
    };

    [Fact]
    public async Task REQ_BT_028_Summary_and_alerts_never_read_every_batch()
    {
        store.Put(Waiting("b-1", "farm-a", PendingStatus.Pending));

        await Service().SummaryAsync(Everyone, default);
        await Service().AlertsAsync(Everyone, default);

        Assert.Equal(0, store.FullReads);
    }

    [Fact]
    public async Task REQ_BT_028_Summary_counts_only_the_callers_farms()
    {
        store.Put(Waiting("b-1", "farm-a", PendingStatus.Pending));
        store.Put(Waiting("b-2", "farm-b", PendingStatus.Pending));
        store.Put(Waiting("b-3", "farm-b", PendingStatus.Dead));

        var summary = await Service().SummaryAsync(FarmA, default);

        Assert.Equal(1, summary.Pending);
        Assert.Equal(0, summary.Blocked);
        Assert.Equal(1, summary.ByState[nameof(BatchState.Created)]);
    }

    [Fact]
    public async Task REQ_BT_028_Summary_states_come_from_the_counts_not_the_rows()
    {
        store.LiveAnswer = new LiveScan(
            [new StateCount("farm-a", nameof(BatchState.Closed), 90000), new StateCount("farm-b", nameof(BatchState.Closed), 7)], []);

        var summary = await Service().SummaryAsync(FarmA, default);

        Assert.Equal(90000, summary.ByState[nameof(BatchState.Closed)]);
        Assert.Equal(0, summary.ByState[nameof(BatchState.Created)]);
    }

    [Fact]
    public async Task REQ_BT_028_Alerts_hold_only_the_callers_farms()
    {
        store.Put(Waiting("b-1", "farm-a", PendingStatus.Dead));
        store.Put(Waiting("b-2", "farm-b", PendingStatus.Dead));

        var alert = Assert.Single(await Service().AlertsAsync(FarmA, default));

        Assert.Equal("b-1", alert.BatchId);
    }
}
