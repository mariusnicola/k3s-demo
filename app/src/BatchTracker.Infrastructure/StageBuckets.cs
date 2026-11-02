using System.Diagnostics;
using System.Globalization;
using System.Text;
using BatchTracker.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BatchTracker.Infrastructure;

/// <summary>One stage's histogram. Plain counters on a fixed array, so recording allocates nothing.</summary>
internal sealed class StageBuckets
{
    /// <summary>1 ms to 60 s in 1, 2.5, 5 steps, thinning past 10 s: a time past the last bound still counts, as +Inf.</summary>
    private static readonly double[] Bounds = [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60];

    private readonly long[] counts = new long[Bounds.Length + 1];
    private long sumTicks;

    public void Add(double seconds)
    {
        var found = Array.BinarySearch(Bounds, seconds);
        Interlocked.Increment(ref counts[found >= 0 ? found : ~found]);
        Interlocked.Add(ref sumTicks, (long)(seconds * TimeSpan.TicksPerSecond));
    }

    public void Write(StringBuilder text, string name, string stage)
    {
        var total = 0L;
        for (var i = 0; i < counts.Length; i++)
        {
            total += Interlocked.Read(ref counts[i]);
            var bound = i < Bounds.Length ? Bounds[i].ToString(CultureInfo.InvariantCulture) : "+Inf";
            text.Append(name).Append("_bucket{stage=\"").Append(stage).Append("\",le=\"").Append(bound).Append("\"} ").Append(total).Append('\n');
        }
        var sum = (double)Interlocked.Read(ref sumTicks) / TimeSpan.TicksPerSecond;
        text.Append(name).Append("_sum{stage=\"").Append(stage).Append("\"} ").Append(sum.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append(name).Append("_count{stage=\"").Append(stage).Append("\"} ").Append(total).Append('\n');
    }
}

/// <summary>
/// Outermost after the correlation id, so a refused or failed call is timed as the caller felt it. Probes and scrapes
/// stay out: they are not calls anyone waits on, and at idle they would be the whole histogram.
/// </summary>
public sealed class ApiStageTimes(RequestDelegate next, IBatchCounters counters)
{
    public Task InvokeAsync(HttpContext context) => IsCall(context.Request.Path) ? TimedAsync(context) : next(context);

    private static bool IsCall(PathString path) =>
        path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/healthz") && !path.StartsWithSegments("/api/ready");

    private async Task TimedAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            counters.Time(Stages.Api, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }
}

public static class StageTimeSetup
{
    public static IApplicationBuilder UseApiStageTimes(this IApplicationBuilder app) => app.UseMiddleware<ApiStageTimes>();
}
