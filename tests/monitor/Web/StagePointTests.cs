using System.Text.Json;
using System.Web;
using Xunit;

/// <summary>The card moves on this frame and never polls, so it has to carry what the 10 s series would.</summary>
public sealed partial class MonitorSocketTests
{
    private static readonly DateTimeOffset StageNow = new(2026, 10, 31, 12, 35, 26, TimeSpan.Zero);
    private static readonly long StageCloses = new DateTimeOffset(2026, 10, 31, 12, 35, 20, TimeSpan.Zero).ToUnixTimeSeconds();

    private static string Range(string metric, string value) =>
        $$$"""{"status":"success","data":{"resultType":"matrix","result":[{"metric":{{{metric}}},"values":[[{{{StageCloses}}},"{{{value}}}"]]}]}}""";

    private static string StageAnswer(string path)
    {
        var query = HttpUtility.ParseQueryString(path.Split('?')[1])["query"]!;
        if (query.StartsWith("prometheus_tsdb")) return """{"status":"success","data":{"result":[{"metric":{},"value":[1,"1"]}]}}""";
        if (query.StartsWith("histogram_quantile(0.5,")) return Range("""{"stage":"api"}""", "0.012");
        if (query.StartsWith("histogram_quantile(0.95,")) return Range("""{"stage":"api"}""", "0.084");
        if (query.Contains("bt_events_pending")) return Range("{}", "3");
        if (query.Contains("bt_batches_created_total")) return Range("{}", "7");
        return """{"status":"success","data":{"result":[]}}""";
    }

    private static Func<string, CancellationToken, Task<string>> StagePrometheus(List<string>? asked = null) => (path, _) =>
    {
        asked?.Add(path);
        return Task.FromResult(StageAnswer(path));
    };

    private StagePoints Points(List<string>? asked = null) => new(new MetricSeries(StagePrometheus(asked)), hub);

    [Fact]
    public async Task REQ_BT_059_An_admin_socket_gets_the_stage_point_in_order_with_nulls_kept()
    {
        using var socket = await Open(Token([OpsAdmin]));
        for (var i = 0; i < 3; i++) await Next(socket);

        await Points().Publish(StageNow, CancellationToken.None);

        using var frame = JsonDocument.Parse(await Next(socket));
        var root = frame.RootElement;
        Assert.Equal("stagePoint", root.GetProperty("type").GetString());
        var stages = root.GetProperty("stages").EnumerateArray().ToArray();
        Assert.Equal(["api", "store", "queue", "worker", "bundle"], stages.Select(s => s.GetProperty("stage").GetString()));
        Assert.Equal(0.012, stages[0].GetProperty("p50").GetDouble());
        Assert.Equal(0.084, stages[0].GetProperty("p95").GetDouble());
        Assert.Equal(JsonValueKind.Null, stages[1].GetProperty("p95").ValueKind);
        Assert.Equal(3, root.GetProperty("pending").GetDouble());
        Assert.Equal(7, root.GetProperty("values").GetProperty("bt_batches_created_total").GetDouble());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("values").GetProperty("bt_dose1_total").ValueKind);
    }

    [Fact]
    public async Task REQ_BT_059_The_stage_point_is_the_last_bucket_of_the_10_s_series()
    {
        using var socket = await Open(Token([OpsAdmin]));
        for (var i = 0; i < 3; i++) await Next(socket);
        var series = await new MetricSeries(StagePrometheus()).Read("10s", StageNow, CancellationToken.None);

        await Points().Publish(StageNow, CancellationToken.None);

        using var frame = JsonDocument.Parse(await Next(socket));
        var last = series.Stages[0].Points[^1];
        Assert.Equal(last.At, frame.RootElement.GetProperty("at").GetDateTimeOffset());
        Assert.Equal(last.P95, frame.RootElement.GetProperty("stages")[0].GetProperty("p95").GetDouble());
        Assert.Equal(series.Pending[^1].Value, frame.RootElement.GetProperty("pending").GetDouble());
    }

    [Fact]
    public async Task REQ_BT_059_With_no_admin_socket_nothing_reads_prometheus()
    {
        var asked = new List<string>();
        using var socket = await Open();
        Assert.Equal("operations", KindOf(await Next(socket)));

        await Points(asked).Publish(StageNow, CancellationToken.None);

        Assert.Empty(asked);
    }

    [Fact]
    public async Task REQ_BT_059_One_stage_point_asks_for_one_bucket_only()
    {
        var asked = new List<string>();
        using var socket = await Open(Token([OpsAdmin]));
        for (var i = 0; i < 3; i++) await Next(socket);

        await Points(asked).Publish(StageNow, CancellationToken.None);

        var ranges = asked.Where(p => p.Contains("query_range")).Select(p => HttpUtility.ParseQueryString(p.Split('?')[1])).ToArray();
        Assert.NotEmpty(ranges);
        Assert.All(ranges, q => Assert.Equal(q["start"], q["end"]));
    }

    [Fact]
    public async Task REQ_BT_059_Prometheus_down_sends_no_stage_point()
    {
        using var socket = await Open(Token([OpsAdmin]));
        for (var i = 0; i < 3; i++) await Next(socket);

        await new StagePoints(new MetricSeries((_, _) => throw new HttpRequestException("refused")), hub).Publish(StageNow, CancellationToken.None);
        hub.Broadcast(operations.Frame());

        Assert.Equal("operations", KindOf(await Next(socket)));
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(5, 1)]
    [InlineData(6, 10)]
    [InlineData(9, 7)]
    public void REQ_BT_059_A_stage_point_goes_6_s_past_each_10_s(int past, int seconds)
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 35, 10 + past, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(seconds), StagePoints.UntilNext(now));
    }
}
