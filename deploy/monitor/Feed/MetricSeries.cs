using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public record MetricPointRow(DateTimeOffset At, double? Value);
public record MetricRow(string Name, double Total, double AveragePerMinute, MetricPointRow[] Points);
public record MetricsReply(string Bucket, bool Available, string? Error, DateTimeOffset? HistoryStart, MetricRow[] Series, StageRow[] Stages, MetricPointRow[] Pending);

/// <summary>Turns Prometheus counters into the Dashboard's buckets. Prometheus answers by its own query path, so the wire stays out of this class.</summary>
public sealed class MetricSeries(Func<string, CancellationToken, Task<string>> prometheus)
{
    private const int Buckets = 120;
    private const string HistoryQuery = "prometheus_tsdb_lowest_timestamp_seconds";
    private const string WorkInFlight = "bt_worker_work_in_flight";
    private const string PassedRate = "sum by (pod) (rate(bt_events_passed_total[1m]))";

    private static readonly string[] Names =
    [
        "bt_events_created_total", "bt_events_passed_total", "bt_events_dead_lettered_total", "bt_events_replayed_total",
        "bt_batches_created_total", "bt_dose1_total", "bt_dose2_total", "bt_inspections_passed_total",
        "bt_inspections_failed_total", "bt_batches_shipped_total", "bt_batches_closed_total", "bt_batches_rejected_total",
        "bt_bundle_items_total", "bt_bundle_item_failures_total"
    ];

    private static readonly Dictionary<string, int> Seconds = new()
    {
        ["10s"] = 10, ["30s"] = 30, ["1m"] = 60, ["30m"] = 1800, ["1h"] = 3600, ["3h"] = 10800, ["1d"] = 86400
    };

    public static bool IsBucket(string? bucket) => bucket is not null && Seconds.ContainsKey(bucket);

    /// <summary>The last `buckets` buckets: a view reads all of them, a live frame only the one that closed.</summary>
    public async Task<MetricsReply> Read(string bucket, DateTimeOffset now, CancellationToken token, int buckets = Buckets)
    {
        try
        {
            var step = Seconds[bucket];
            var end = now.ToUnixTimeSeconds() / step * step;
            var historyStart = await HistoryStart(token);
            var rows = await Task.WhenAll(Names.Select(name => Row(name, step, end, buckets, historyStart, token)));
            var (stages, pending) = await new StageSeries(Ask).Read(step, end, buckets, historyStart, token);
            return new MetricsReply(bucket, true, null, historyStart, rows, stages, pending);
        }
        catch (Exception problem) when (IsDown(problem))
        {
            return new MetricsReply(bucket, false, problem.Message, null, [], [], []);
        }
    }

    public static bool IsDown(Exception problem) => problem is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException;

    /// <summary>The monitor's prometheus check: throws when it cannot answer.</summary>
    public Task Ready(CancellationToken token) => HistoryStart(token);

    /// <summary>The minute that closed before now. A metric Prometheus has nothing for is left out, because a missing value is not zero.</summary>
    public async Task<Dictionary<string, double>> Closed(DateTimeOffset now, CancellationToken token)
    {
        var end = now.ToUnixTimeSeconds() / 60 * 60;
        var read = await Task.WhenAll(Names.Select(async name =>
        {
            var query = Uri.EscapeDataString($"sum(increase({name}[60s]))");
            using var document = await Ask($"api/v1/query?query={query}&time={end}", token);
            var result = document.RootElement.GetProperty("data").GetProperty("result");
            return (name, value: result.GetArrayLength() == 0 ? (double?)null : Math.Round(Number(result[0].GetProperty("value")[1])));
        }));
        return read.Where(r => r.value is not null).ToDictionary(r => r.name, r => r.value!.Value);
    }

    /// <summary>
    /// Busy per Worker pod: passed an event in the last minute, or has work in flight now. Either one saying yes is enough,
    /// and no takes both, so a read that failed or a pod missing from one answer is unknown and never idle.
    /// </summary>
    public async Task<Dictionary<string, bool?>> WorkerBusy(CancellationToken token)
    {
        var reads = await Task.WhenAll(ByPod(WorkInFlight, token), ByPod(PassedRate, token));
        var (inFlight, passed) = (reads[0], reads[1]);
        if (inFlight is null && passed is null) throw new InvalidOperationException("Prometheus answered neither busy read");
        string[] pods = [.. inFlight?.Keys ?? Enumerable.Empty<string>(), .. passed?.Keys ?? Enumerable.Empty<string>()];
        return pods.Distinct().ToDictionary(pod => pod, pod => Busy(pod, inFlight, passed));
    }

    private static bool? Busy(string pod, Dictionary<string, double>? inFlight, Dictionary<string, double>? passed)
    {
        double? working = inFlight?.GetValueOrDefault(pod);
        double? passing = passed?.GetValueOrDefault(pod);
        if (working > 0 || passing > 0) return true;
        return inFlight?.ContainsKey(pod) == true && passed?.ContainsKey(pod) == true ? false : null;
    }

    /// <summary>Null when the read failed, so the caller can tell that from a pod with nothing to report.</summary>
    private async Task<Dictionary<string, double>?> ByPod(string query, CancellationToken token)
    {
        try
        {
            using var document = await Ask($"api/v1/query?query={Uri.EscapeDataString(query)}", token);
            return document.RootElement.GetProperty("data").GetProperty("result").EnumerateArray()
                .ToDictionary(r => r.GetProperty("metric").GetProperty("pod").GetString()!, r => Number(r.GetProperty("value")[1]));
        }
        catch (Exception problem) when (IsDown(problem))
        {
            return null;
        }
    }

    private async Task<DateTimeOffset> HistoryStart(CancellationToken token)
    {
        using var document = await Ask($"api/v1/query?query={HistoryQuery}", token);
        var result = document.RootElement.GetProperty("data").GetProperty("result");
        if (result.GetArrayLength() == 0) throw new InvalidOperationException("Prometheus reports no history start");
        return DateTimeOffset.FromUnixTimeSeconds((long)Number(result[0].GetProperty("value")[1]));
    }

    private async Task<MetricRow> Row(string name, int step, long end, int buckets, DateTimeOffset historyStart, CancellationToken token)
    {
        var start = end - (buckets - 1) * step;
        var query = Uri.EscapeDataString($"sum(increase({name}[{step}s]))");
        using var document = await Ask($"api/v1/query_range?query={query}&start={start}&end={end}&step={step}", token);

        var values = new Dictionary<long, double>();
        foreach (var result in document.RootElement.GetProperty("data").GetProperty("result").EnumerateArray())
            foreach (var pair in result.GetProperty("values").EnumerateArray())
                values[(long)pair[0].GetDouble()] = Math.Round(Number(pair[1]));

        var points = Enumerable.Range(0, buckets).Select(i =>
        {
            var closes = start + i * step;
            var at = DateTimeOffset.FromUnixTimeSeconds(closes - step);
            return new MetricPointRow(at, at >= historyStart && values.TryGetValue(closes, out var value) ? value : null);
        }).ToArray();

        var total = points.Sum(p => p.Value ?? 0);
        var from = points[0].At > historyStart ? points[0].At : historyStart;
        var minutes = (DateTimeOffset.FromUnixTimeSeconds(end) - from).TotalMinutes;
        return new MetricRow(name, total, minutes > 0 ? total / minutes : 0, points);
    }

    private static double Number(JsonElement text) => double.Parse(text.GetString()!, CultureInfo.InvariantCulture);

    private async Task<JsonDocument> Ask(string path, CancellationToken token)
    {
        var document = JsonDocument.Parse(await prometheus(path, token));
        if (document.RootElement.GetProperty("status").GetString() == "success") return document;
        var reason = document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "no reason given";
        document.Dispose();
        throw new InvalidOperationException($"Prometheus refused the query: {reason}");
    }
}

/// <summary>Pushes each closed minute to admin sockets, so the Performance panel moves without reading the series again.</summary>
public sealed class MetricPoints(MetricSeries series, SocketHub hub)
{
    /// <summary>A scrape every 15 s has to land after the minute closes, or its last counts are missing.</summary>
    private const int SecondsPastMinute = 20;

    public static TimeSpan UntilNext(DateTimeOffset now)
    {
        var past = (int)(now.ToUnixTimeSeconds() % 60);
        return TimeSpan.FromSeconds(past < SecondsPastMinute ? SecondsPastMinute - past : 60 + SecondsPastMinute - past);
    }

    /// <summary>Prometheus down sends nothing: the panel shows unknown, never zero, and the prometheus check says why.</summary>
    public async Task Publish(DateTimeOffset now, CancellationToken token)
    {
        try
        {
            var values = await series.Closed(now, token);
            hub.Broadcast(new { type = "metricPoint", at = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 60 * 60 - 60), values });
        }
        catch (Exception problem) when (MetricSeries.IsDown(problem))
        {
        }
    }
}

public static class MetricRoutes
{
    /// <summary>The session is checked first, so an anonymous caller learns nothing, not even which buckets exist.</summary>
    public static void Map(WebApplication app, SessionReader sessions, MetricSeries series, ProbeLimits limits)
    {
        app.MapGet("/metrics/series", async (HttpContext context) =>
        {
            if (await sessions.CookieAdminUntil(context.Request) is null)
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign in again", detail: "An admin session is required.");

            string? bucket = context.Request.Query["bucket"];
            if (!MetricSeries.IsBucket(bucket))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown bucket", detail: "Use 10s, 30s, 1m, 30m, 1h, 3h or 1d.");

            var reply = await series.Read(bucket!, DateTimeOffset.UtcNow, context.RequestAborted);
            return Results.Json(reply, new JsonSerializerOptions(JsonSerializerDefaults.Web), statusCode: reply.Available ? 200 : 503);
        }).AddEndpointFilter(limits);
    }
}
