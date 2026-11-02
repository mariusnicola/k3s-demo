using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using BatchTracker.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BatchTracker.Infrastructure;

/// <summary>
/// Every series is written from the start at zero: Prometheus cannot see the rise of a series it first sees at 1.
/// One meter per instance, so hosts sharing a process keep separate counts.
/// </summary>
public sealed class MeterCounters : IBatchCounters, IDisposable
{
    private const string FarmLabel = "farm";
    private const string StageLabel = "stage";

    private readonly Meter meter = new("BatchTracker");
    private readonly MeterListener listener = new();
    private readonly IReadOnlyList<string> farms;
    private readonly Dictionary<string, Counter<long>> counters;
    private readonly ConcurrentDictionary<(string Name, string Farm), long> totals = new();
    private readonly string? gauge;
    private readonly IPendingNow? pending;
    private readonly Histogram<double> stageSeconds;
    private readonly Dictionary<string, StageBuckets> stages;
    private readonly Dictionary<string, KeyValuePair<string, object?>> stageTags;
    private long inFlight;

    public MeterCounters(
        IReadOnlyList<string> names, IReadOnlyList<string> farms, string? gauge = null, IReadOnlyList<string>? stages = null, IPendingNow? pending = null)
    {
        this.farms = farms;
        this.gauge = gauge;
        this.pending = pending;
        counters = names.ToDictionary(n => n, n => meter.CreateCounter<long>(n));
        stageSeconds = meter.CreateHistogram<double>(MetricNames.StageSeconds);
        this.stages = (stages ?? []).ToDictionary(s => s, _ => new StageBuckets());
        stageTags = this.stages.Keys.ToDictionary(s => s, s => new KeyValuePair<string, object?>(StageLabel, s));
        listener.InstrumentPublished = (instrument, subscribed) =>
        {
            if (instrument.Meter == meter) subscribed.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == FarmLabel) totals.AddOrUpdate((instrument.Name, (string)tag.Value!), value, (_, total) => total + value);
        });
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == StageLabel) this.stages[(string)tag.Value!].Add(value);
        });
        listener.Start();
    }

    /// <summary>The farm comes from a stored event, and a label outside the known list would grow the series without limit.</summary>
    public void Count(string name, string farm)
    {
        if (!counters.TryGetValue(name, out var counter)) throw new ArgumentException($"{name} is not exported here.", nameof(name));
        if (farms.Contains(farm)) counter.Add(1, new KeyValuePair<string, object?>(FarmLabel, farm));
    }

    /// <summary>The tag is built once per stage, so a call boxes nothing.</summary>
    public void Time(string stage, double seconds)
    {
        if (!stageTags.TryGetValue(stage, out var tag)) throw new ArgumentException($"{stage} is not a stage of this service.", nameof(stage));
        stageSeconds.Record(seconds, tag);
    }

    public void WorkStarted() => Interlocked.Increment(ref inFlight);

    public void WorkEnded() => Interlocked.Decrement(ref inFlight);

    public string Text()
    {
        var text = new StringBuilder();
        foreach (var name in counters.Keys)
        {
            text.Append("# TYPE ").Append(name).Append(" counter\n");
            foreach (var farm in farms)
                text.Append(name).Append("{farm=\"").Append(farm).Append("\"} ").Append(totals.GetValueOrDefault((name, farm))).Append('\n');
        }
        if (gauge is not null) text.Append("# TYPE ").Append(gauge).Append(" gauge\n").Append(gauge).Append(' ').Append(Interlocked.Read(ref inFlight)).Append('\n');
        if (stages.Count > 0) text.Append("# TYPE ").Append(MetricNames.StageSeconds).Append(" histogram\n");
        foreach (var (stage, buckets) in stages) buckets.Write(text, MetricNames.StageSeconds, stage);
        if (pending?.PendingNow is { } count)
            text.Append("# TYPE ").Append(MetricNames.EventsPending).Append(" gauge\n").Append(MetricNames.EventsPending).Append(' ').Append(count).Append('\n');
        return text.ToString();
    }

    public void Dispose()
    {
        listener.Dispose();
        meter.Dispose();
    }
}

/// <summary>Runs after sign-in and limits, so a refused call never reads as work.</summary>
public sealed class WorkInFlight(MeterCounters counters) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        counters.WorkStarted();
        try
        {
            return await next(context);
        }
        finally
        {
            counters.WorkEnded();
        }
    }
}

public static class CounterSetup
{
    public static IServiceCollection AddBatchCounters(this IServiceCollection services, IReadOnlyList<string> names, IReadOnlyList<string> stages, string? gauge = null)
    {
        services.AddSingleton(s => new MeterCounters(names, DemoUsers.KnownFarms, gauge, stages, s.GetService<IPendingNow>()));
        return services.AddSingleton<IBatchCounters>(s => s.GetRequiredService<MeterCounters>());
    }

    /// <summary>The ingress forwards every path of the Worker host and always sets this header; a direct scrape never does.</summary>
    public static void MapMetrics(this IEndpointRouteBuilder app) =>
        app.MapGet("/metrics", (MeterCounters counters, HttpRequest request) =>
                request.Headers.ContainsKey("X-Forwarded-For")
                    ? Results.NotFound()
                    : Results.Text(counters.Text(), "text/plain; version=0.0.4; charset=utf-8"))
            .AllowAnonymous()
            .LimitedAsHealth();
}
