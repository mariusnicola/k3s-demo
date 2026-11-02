using BatchTracker.Application;
using BatchTracker.Domain;

namespace BatchTracker.UnitTests;

/// <summary>Each item of a bundle answers as its single route does, and no item touches another.</summary>
public class BatchBundleTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly Caller Line = DemoUsers.LineMachine;
    private static readonly Stamp Made = new("ada", "trace-0", Start);

    private readonly FakeStore store = new();
    private readonly FakeCounters counters = new();
    private readonly SeenHops hops = new();

    private BatchBundle Bundle() => new(store, hops, new Clock(Start), counters);

    private sealed class SeenHops : IHopReporter
    {
        public List<(string BatchId, string? Correlation)> Seen { get; } = [];
        public void Report(Hop hop) => Seen.Add((hop.BatchId, Correlation.Current));
    }

    private Versioned Row(BatchState state = BatchState.Created)
    {
        var batch = BatchMachine.Create(Guid.NewGuid().ToString(), "farm-a", "p", Made) with { Pending = null, State = state };
        return store.Put(batch);
    }

    private static BundleItem Advance(Versioned row, string to, string? etag = null) =>
        new(BundleKinds.Advance, FarmId: row.Batch.FarmId, Id: row.Batch.Id, To: to, IfMatch: etag ?? row.ETag);

    private static BundleItem Create(string key, string product = "p") =>
        new(BundleKinds.Create, FarmId: "farm-a", Product: product, Key: key);

    private Task<IReadOnlyList<BundleAnswer>> Run(params BundleItem[] items) => Bundle().RunAsync(Line, items, default);

    [Fact]
    public async Task REQ_BT_031_A_mixed_bundle_answers_each_item_in_order_and_one_failure_stops_nothing()
    {
        var good = Row();
        var ready = Row(BatchState.Dose2);

        var answers = await Run(
            Create("one"),
            new BundleItem("melt"),
            Advance(good, nameof(BatchState.Dose1)),
            new BundleItem(BundleKinds.Inspect, FarmId: "farm-a", Id: ready.Batch.Id, Passed: true, IfMatch: ready.ETag));

        Assert.Equal(4, answers.Count);
        Assert.Equal(BatchState.Created, answers[0].Outcome.Value!.Batch.State);
        Assert.Equal(Failure.Validation, answers[1].Outcome.Failure);
        Assert.Equal(BatchState.Dose1, store.Read(good.Batch.Id).State);
        Assert.Equal(BatchState.Inspected, store.Read(ready.Batch.Id).State);
    }

    [Fact]
    public async Task REQ_BT_031_A_bundle_is_one_read_and_one_write()
    {
        await Run(Create("one"), Advance(Row(), nameof(BatchState.Dose1)), Advance(Row(), nameof(BatchState.Dose1)));

        Assert.Equal(1, store.ManyReads);
        Assert.Equal(1, store.ManyWrites);
    }

    [Fact]
    public async Task REQ_BT_031_Each_single_route_refusal_comes_back_on_its_item()
    {
        var pending = store.Put(BatchMachine.Create(Guid.NewGuid().ToString(), "farm-a", "p", Made));
        var row = Row();
        var olivia = DemoUsers.Find("olivia")!;

        var answers = await Bundle().RunAsync(olivia,
        [
            Advance(row, nameof(BatchState.Dose1)) with { FarmId = "farm-c" },
            Advance(row, "Melted"),
            Advance(row, nameof(BatchState.Dose1)) with { IfMatch = null },
            Advance(row, nameof(BatchState.Dose1), "\"stale\""),
            Advance(pending, nameof(BatchState.Dose1)),
            Advance(row, nameof(BatchState.Dose1)) with { Id = "not-a-guid" }
        ], default);

        Assert.Equal(
            [Failure.Forbidden, Failure.Validation, Failure.MissingIfMatch, Failure.Stale, Failure.Conflict, Failure.NotFound],
            answers.Select(a => a.Outcome.Failure!.Value));
    }

    [Fact]
    public async Task REQ_BT_031_Inspect_without_its_permission_is_refused_on_the_item_alone()
    {
        var ready = Row(BatchState.Dose2);
        var row = Row();
        var olivia = DemoUsers.Find("olivia")!;

        var answers = await Bundle().RunAsync(olivia,
        [
            new BundleItem(BundleKinds.Inspect, FarmId: "farm-a", Id: ready.Batch.Id, Passed: true, IfMatch: ready.ETag),
            Advance(row, nameof(BatchState.Dose1))
        ], default);

        Assert.Equal(Failure.Forbidden, answers[0].Outcome.Failure);
        Assert.Equal(BatchState.Dose2, store.Read(ready.Batch.Id).State);
        Assert.Null(answers[1].Outcome.Failure);
    }

    [Fact]
    public async Task REQ_BT_031_A_repeated_key_gives_the_batch_held_and_counts_nothing_more()
    {
        var first = await Run(Create("again"));
        var seen = counters.Counted.Count(c => c.Name == MetricNames.BatchesCreated);

        var second = await Run(Create("again"));

        Assert.Equal(first[0].Outcome.Value!.Batch.Id, second[0].Outcome.Value!.Batch.Id);
        Assert.Equal(seen, counters.Counted.Count(c => c.Name == MetricNames.BatchesCreated));
        Assert.Single(hops.Seen);
    }

    [Fact]
    public async Task REQ_BT_046_Creates_in_a_bundle_count_once_per_batch_on_its_farm_as_the_single_route_does()
    {
        var other = new BundleItem(BundleKinds.Create, FarmId: "farm-b", Product: "p", Key: "two");

        await Run(Create("one"), other, Create("three"), new BundleItem("melt", FarmId: "farm-a"));
        await Run(Create("one"));

        Assert.Equal(
            [(MetricNames.BatchesCreated, "farm-a"), (MetricNames.BatchesCreated, "farm-a"), (MetricNames.BatchesCreated, "farm-b")],
            counters.Counted.Where(c => c.Name == MetricNames.BatchesCreated).OrderBy(c => c.Farm));
    }

    [Fact]
    public async Task REQ_BT_031_A_key_used_with_another_body_is_refused_on_the_item()
    {
        await Run(Create("again"));

        var answers = await Run(Create("again", "other"));

        Assert.Equal(Failure.KeyReused, answers[0].Outcome.Failure);
    }

    [Fact]
    public async Task REQ_BT_031_A_write_lost_to_another_writer_is_stale_on_its_item_alone()
    {
        var lost = Row();
        var kept = Row();
        store.BeforeReplace = _ =>
        {
            store.Put(store.Read(lost.Batch.Id));
            return Task.CompletedTask;
        };

        var answers = await Run(Advance(lost, nameof(BatchState.Dose1)), Advance(kept, nameof(BatchState.Dose1)));

        Assert.Equal(Failure.Stale, answers[0].Outcome.Failure);
        Assert.Null(answers[1].Outcome.Failure);
    }

    [Fact]
    public async Task REQ_BT_052_Each_item_keeps_its_own_correlation_id_on_its_event_and_hop()
    {
        var row = Row();
        var other = Row();

        var answers = await Run(
            Advance(row, nameof(BatchState.Dose1)) with { CorrelationId = "walk-one" },
            Advance(other, nameof(BatchState.Dose1)) with { CorrelationId = "walk-two" });

        Assert.Equal(["walk-one", "walk-two"], answers.Select(a => a.CorrelationId));
        Assert.Equal("walk-one", store.Read(row.Batch.Id).Pending!.CorrelationId);
        Assert.Equal("walk-two", store.Read(other.Batch.Id).Pending!.CorrelationId);
        Assert.Contains((row.Batch.Id, (string?)"walk-one"), hops.Seen);
        Assert.Contains((other.Batch.Id, (string?)"walk-two"), hops.Seen);
    }

    [Fact]
    public async Task REQ_BT_052_A_malformed_or_missing_id_is_replaced_as_at_the_edge()
    {
        var answers = await Run(Create("a") with { CorrelationId = "bad id" }, Create("b"));

        Assert.All(answers, a => Assert.True(Correlation.IsValid(a.CorrelationId)));
        Assert.NotEqual("bad id", answers[0].CorrelationId);
        Assert.NotEqual(answers[0].CorrelationId, answers[1].CorrelationId);
    }

    [Fact]
    public async Task REQ_BT_046_Items_failures_and_the_bundle_time_are_recorded()
    {
        await Run(Create("one"), new BundleItem("melt", FarmId: "farm-b"));

        Assert.Equal(2, counters.Counted.Count(c => c.Name == MetricNames.BundleItems));
        Assert.Equal([(MetricNames.BundleItemFailures, "farm-b")], counters.Counted.Where(c => c.Name == MetricNames.BundleItemFailures));
        Assert.Single(counters.Times, t => t.Stage == Stages.Bundle);
    }

    [Fact]
    public void REQ_BT_031_The_item_limit_holds_every_walk_the_line_can_have_in_flight()
    {
        Assert.Equal(BatchBundle.MaxItems, new BatchTracker.Line.LineOptions().MaxInFlight);
    }
}
