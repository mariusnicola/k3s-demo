using System.Globalization;
using System.Web;
using Xunit;

/// <summary>The contract the Performance card reads, against a Prometheus that answers from a table.</summary>
public class StageSeriesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 34, 56, TimeSpan.Zero);
    private const string Empty = """{"status":"success","data":{"resultType":"matrix","result":[]}}""";

    private static string Lowest(DateTimeOffset at) =>
        $$$"""{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1,"{{{at.ToUnixTimeSeconds()}}}"]}]}}""";

    private static string ByStage(params (string Stage, long At, string Value)[] rows)
    {
        var results = rows.Select(r => $$$"""{"metric":{"stage":"{{{r.Stage}}}"},"values":[[{{{r.At}}},"{{{r.Value}}}"]]}""");
        return $$$"""{"status":"success","data":{"resultType":"matrix","result":[{{{string.Join(",", results)}}}]}}""";
    }

    private static string Single(long at, string value) =>
        $$$"""{"status":"success","data":{"resultType":"matrix","result":[{"metric":{},"values":[[{{{at}}},"{{{value}}}"]]}]}}""";

    private static Func<string, CancellationToken, Task<string>> Prometheus(
        string? p50 = null, string? p95 = null, string? pending = null, DateTimeOffset? historyStart = null, List<string>? asked = null) =>
        (path, _) =>
        {
            var query = HttpUtility.ParseQueryString(path.Split('?')[1])["query"]!;
            asked?.Add(query);
            if (query.StartsWith("prometheus_tsdb")) return Task.FromResult(Lowest(historyStart ?? Now.AddDays(-200)));
            if (query.StartsWith("histogram_quantile(0.5,")) return Task.FromResult(p50 ?? Empty);
            if (query.StartsWith("histogram_quantile(0.95,")) return Task.FromResult(p95 ?? Empty);
            return Task.FromResult(query.Contains("bt_events_pending") ? pending ?? Empty : Empty);
        };

    private static Task<MetricsReply> Read(string bucket, Func<string, CancellationToken, Task<string>> prometheus) =>
        new MetricSeries(prometheus).Read(bucket, Now, CancellationToken.None);

    /// <summary>The bucket closing at the last whole one, as a Unix time, and the `at` it shows as.</summary>
    private static (long Closes, DateTimeOffset At) Last(int step)
    {
        var closes = Now.ToUnixTimeSeconds() / step * step;
        return (closes, DateTimeOffset.FromUnixTimeSeconds(closes - step));
    }

    [Theory]
    [InlineData("10s")]
    [InlineData("30s")]
    public void REQ_BT_059_The_fine_buckets_are_served(string bucket)
    {
        Assert.True(MetricSeries.IsBucket(bucket));
    }

    [Fact]
    public async Task REQ_BT_059_Every_stage_comes_back_in_order_with_nothing_recorded()
    {
        var reply = await Read("1m", Prometheus());

        Assert.Equal(["api", "store", "queue", "worker", "bundle"], reply.Stages.Select(s => s.Stage));
        Assert.All(reply.Stages, stage =>
        {
            Assert.Equal(120, stage.Points.Length);
            Assert.All(stage.Points, p => Assert.True(p.P50 is null && p.P95 is null));
        });
        Assert.Equal(120, reply.Pending.Length);
        Assert.All(reply.Pending, p => Assert.Null(p.Value));
    }

    [Fact]
    public async Task REQ_BT_059_Stage_and_pending_points_sit_on_the_counters_times()
    {
        var reply = await Read("1m", Prometheus());

        var counters = reply.Series[0].Points.Select(p => p.At).ToArray();
        Assert.All(reply.Stages, stage => Assert.Equal(counters, stage.Points.Select(p => p.At)));
        Assert.Equal(counters, reply.Pending.Select(p => p.At));
    }

    [Theory]
    [InlineData("10s", 10)]
    [InlineData("1m", 60)]
    public async Task REQ_BT_059_Each_query_reads_exactly_one_bucket(string bucket, int seconds)
    {
        var asked = new List<string>();

        await Read(bucket, Prometheus(asked: asked));

        Assert.Contains($"histogram_quantile(0.5, sum by (stage, le) (increase(bt_stage_seconds_bucket[{seconds}s])))", asked);
        Assert.Contains($"histogram_quantile(0.95, sum by (stage, le) (increase(bt_stage_seconds_bucket[{seconds}s])))", asked);
        Assert.Contains($"max(max_over_time(bt_events_pending[{seconds}s]))", asked);
    }

    [Fact]
    public async Task REQ_BT_059_Quantiles_come_back_in_seconds_per_stage()
    {
        var (closes, at) = Last(60);

        var reply = await Read("1m", Prometheus(ByStage(("api", closes, "0.012")), ByStage(("api", closes, "0.084"), ("queue", closes, "1.5"))));

        var api = reply.Stages.Single(s => s.Stage == "api").Points.Single(p => p.At == at);
        Assert.Equal(0.012, api.P50);
        Assert.Equal(0.084, api.P95);
        var queue = reply.Stages.Single(s => s.Stage == "queue").Points.Single(p => p.At == at);
        Assert.Null(queue.P50);
        Assert.Equal(1.5, queue.P95);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("+Inf")]
    public async Task REQ_BT_059_A_quantile_with_no_samples_is_null_and_never_zero(string answer)
    {
        var (closes, at) = Last(60);

        var reply = await Read("1m", Prometheus(p95: ByStage(("api", closes, answer))));

        Assert.Null(reply.Stages[0].Points.Single(p => p.At == at).P95);
    }

    [Fact]
    public async Task REQ_BT_059_Pending_is_the_peak_of_its_bucket()
    {
        var (closes, at) = Last(60);

        var reply = await Read("1m", Prometheus(pending: Single(closes, "37")));

        Assert.Equal(37, reply.Pending.Single(p => p.At == at).Value);
    }

    [Fact]
    public async Task REQ_BT_059_Before_the_history_starts_everything_is_null()
    {
        var (closes, at) = Last(60);

        var reply = await Read("1m", Prometheus(p95: ByStage(("api", closes, "1")), pending: Single(closes, "3"), historyStart: at.AddMinutes(1)));

        Assert.Null(reply.Stages[0].Points.Single(p => p.At == at).P95);
        Assert.Null(reply.Pending.Single(p => p.At == at).Value);
    }

    [Fact]
    public async Task REQ_BT_059_Prometheus_down_leaves_both_lists_empty_and_says_why()
    {
        var reply = await Read("1m", (_, _) => throw new HttpRequestException("refused"));

        Assert.False(reply.Available);
        Assert.Empty(reply.Stages);
        Assert.Empty(reply.Pending);
    }
}
