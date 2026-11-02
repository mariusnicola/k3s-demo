using BatchTracker.Application;
using BatchTracker.Infrastructure;

namespace BatchTracker.UnitTests;

public class StageTimeTests
{
    private const string Line = "bt_stage_seconds";

    private static MeterCounters Counters(params string[] stages) => new([], ["farm-a"], stages: stages);

    private static string Bucket(string stage, string le, long count) => $"{Line}_bucket{{stage=\"{stage}\",le=\"{le}\"}} {count}\n";

    [Fact]
    public void REQ_BT_059_A_declared_stage_is_written_from_the_start_at_zero_and_covers_1_ms_to_60_s()
    {
        using var counters = Counters(Stages.Store);

        var text = counters.Text();

        Assert.Contains($"# TYPE {Line} histogram\n", text);
        Assert.Contains(Bucket(Stages.Store, "0.001", 0), text);
        Assert.Contains(Bucket(Stages.Store, "60", 0), text);
        Assert.Contains(Bucket(Stages.Store, "+Inf", 0), text);
        Assert.Contains($"{Line}_sum{{stage=\"store\"}} 0\n", text);
        Assert.Contains($"{Line}_count{{stage=\"store\"}} 0\n", text);
    }

    [Fact]
    public void REQ_BT_059_Buckets_are_cumulative_and_a_value_on_a_bound_counts_in_it()
    {
        using var counters = Counters(Stages.Api);

        counters.Time(Stages.Api, 0.001);
        counters.Time(Stages.Api, 0.02);
        counters.Time(Stages.Api, 90);

        var text = counters.Text();
        Assert.Contains(Bucket(Stages.Api, "0.001", 1), text);
        Assert.Contains(Bucket(Stages.Api, "0.01", 1), text);
        Assert.Contains(Bucket(Stages.Api, "0.025", 2), text);
        Assert.Contains(Bucket(Stages.Api, "60", 2), text);
        Assert.Contains(Bucket(Stages.Api, "+Inf", 3), text);
        Assert.Contains($"{Line}_sum{{stage=\"api\"}} 90.021\n", text);
        Assert.Contains($"{Line}_count{{stage=\"api\"}} 3\n", text);
    }

    [Fact]
    public void REQ_BT_059_A_service_exports_only_the_stages_it_does()
    {
        using var counters = Counters(Stages.Store);

        Assert.DoesNotContain("stage=\"api\"", counters.Text());
        Assert.Throws<ArgumentException>(() => counters.Time(Stages.Api, 1));
    }

    [Fact]
    public void REQ_BT_059_Each_service_declares_the_stages_it_does()
    {
        Assert.Equal([Stages.Api, Stages.Store, Stages.Bundle], MetricNames.ApiStages);
        Assert.Equal([Stages.Store, Stages.Queue, Stages.Worker], MetricNames.WorkerStages);
        Assert.Equal([Stages.Store], MetricNames.DispatcherStages);
    }

    [Fact]
    public void REQ_BT_059_Timing_allocates_nothing_on_the_hot_path()
    {
        using var counters = Counters(Stages.Worker);
        counters.Time(Stages.Worker, 0.1);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) counters.Time(Stages.Worker, i / 1000.0);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void REQ_BT_059_Pending_is_exported_only_while_something_counts_it()
    {
        var source = new PendingSource();
        using var counters = new MeterCounters([], ["farm-a"], pending: source);

        Assert.DoesNotContain(MetricNames.EventsPending, counters.Text());

        source.PendingNow = 7;

        Assert.Contains($"# TYPE {MetricNames.EventsPending} gauge\n{MetricNames.EventsPending} 7\n", counters.Text());
    }

    private sealed class PendingSource : IPendingNow
    {
        public long? PendingNow { get; set; }
    }
}
