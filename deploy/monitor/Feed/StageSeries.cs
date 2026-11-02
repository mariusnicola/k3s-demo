using System.Globalization;
using System.Text.Json;

public record StagePointRow(DateTimeOffset At, double? P50, double? P95);
public record StageRow(string Stage, StagePointRow[] Points);

/// <summary>
/// Pushes each closed 10 s bucket to admin sockets, so the card moves without polling. It is the 10 s series cut to its
/// last bucket, read by the same code, so the two cannot differ. With no admin socket open nothing reads Prometheus.
/// </summary>
public sealed class StagePoints(MetricSeries series, SocketHub hub)
{
    private const int Step = 10;

    /// <summary>A scrape every 5 s has to land after the bucket closes, or its last counts are missing.</summary>
    private const int SecondsPastBucket = 6;

    public static TimeSpan UntilNext(DateTimeOffset now)
    {
        var past = (int)(now.ToUnixTimeSeconds() % Step);
        return TimeSpan.FromSeconds(past < SecondsPastBucket ? SecondsPastBucket - past : Step + SecondsPastBucket - past);
    }

    /// <summary>Prometheus down sends nothing: the card keeps what it has, and the prometheus check says why.</summary>
    public async Task Publish(DateTimeOffset now, CancellationToken token)
    {
        if (!hub.AdminOpen) return;
        var reply = await series.Read("10s", now, token, buckets: 1);
        if (reply.Available) hub.Broadcast(Frame(reply));
    }

    private static object Frame(MetricsReply reply) => new
    {
        type = "stagePoint",
        at = reply.Pending[0].At,
        stages = reply.Stages.Select(s => new { stage = s.Stage, p50 = s.Points[0].P50, p95 = s.Points[0].P95 }).ToArray(),
        pending = reply.Pending[0].Value,
        values = reply.Series.ToDictionary(row => row.Name, row => row.Points[0].Value)
    };
}

/// <summary>Per stage timing and the pending peak for the Performance card, on the same bucket times as the counters.</summary>
public sealed class StageSeries(Func<string, CancellationToken, Task<JsonDocument>> ask)
{
    private static readonly string[] Stages = ["api", "store", "queue", "worker", "bundle"];

    /// <summary>One query per quantile, grouped by stage, so a read costs two queries and not one per stage.</summary>
    public async Task<(StageRow[] Stages, MetricPointRow[] Pending)> Read(int step, long end, int buckets, DateTimeOffset historyStart, CancellationToken token)
    {
        var start = end - (buckets - 1) * step;
        var reads = await Task.WhenAll(
            Range(Quantile(0.5, step), start, end, step, token),
            Range(Quantile(0.95, step), start, end, step, token),
            Range($"max(max_over_time(bt_events_pending[{step}s]))", start, end, step, token));
        var (p50, p95, pending) = (reads[0], reads[1], reads[2]);
        var closes = Enumerable.Range(0, buckets).Select(i => start + i * step).ToArray();

        var stages = Stages.Select(stage => new StageRow(stage, closes.Select(close =>
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(close - step);
            return at < historyStart
                ? new StagePointRow(at, null, null)
                : new StagePointRow(at, Value(p50, stage, close), Value(p95, stage, close));
        }).ToArray())).ToArray();
        var peaks = closes.Select(close =>
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(close - step);
            return new MetricPointRow(at, at < historyStart ? null : Value(pending, "", close));
        }).ToArray();
        return (stages, peaks);
    }

    private static string Quantile(double quantile, int step) => string.Create(CultureInfo.InvariantCulture,
        $"histogram_quantile({quantile}, sum by (stage, le) (increase(bt_stage_seconds_bucket[{step}s])))");

    private static double? Value(Dictionary<(string, long), double> read, string stage, long close) =>
        read.TryGetValue((stage, close), out var value) ? value : null;

    /// <summary>Prometheus says NaN for a bucket with no samples: left out here, because no samples is not zero.</summary>
    private async Task<Dictionary<(string Stage, long At), double>> Range(string query, long start, long end, int step, CancellationToken token)
    {
        using var document = await ask($"api/v1/query_range?query={Uri.EscapeDataString(query)}&start={start}&end={end}&step={step}", token);
        var values = new Dictionary<(string, long), double>();
        foreach (var result in document.RootElement.GetProperty("data").GetProperty("result").EnumerateArray())
        {
            var stage = result.GetProperty("metric").TryGetProperty("stage", out var name) ? name.GetString()! : "";
            foreach (var pair in result.GetProperty("values").EnumerateArray())
                if (double.TryParse(pair[1].GetString(), CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
                    values[(stage, (long)pair[0].GetDouble())] = value;
        }
        return values;
    }
}
