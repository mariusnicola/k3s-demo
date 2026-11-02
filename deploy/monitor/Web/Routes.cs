using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public record Check(string Name, bool Healthy, string? Error);
public record NodeRow(string Name, string Role, bool Ready, int? AllocatableMemoryMi, int? CpuUsageMilli, int? MemoryUsageMi);
public record PodRow(string Namespace, string Name, string Node, string Phase, bool Ready, int CpuLimitMilli, int MemoryLimitMi, int? CpuUsageMilli, int? MemoryUsageMi, bool Terminating = false, bool? Busy = null);
public record WorkerFleet(int Max);
public record Report(DateTimeOffset CheckedAt, bool Healthy, Check[] Checks, NodeRow[] Nodes, PodRow[] Pods, CapRow[] Caps, string[] CapIssues, string? DispatcherLeader = null, string? Store = null, WorkerFleet? Workers = null);

/// <summary>Per client, so a flood from one caller leaves every other probe alone.</summary>
public sealed class ProbeLimits(Func<HttpContext, string> client, int burst, int perSecond) : IEndpointFilter
{
    private readonly PartitionedRateLimiter<string> buckets = SocketLimits.Buckets(burst, perSecond);

    public static ProbeLimits Standard(SocketLimits sockets) => new(sockets.Client, burst: 40, perSecond: 20);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        using var lease = buckets.AttemptAcquire(client(context.HttpContext));
        if (lease.IsAcquired) return await next(context);
        context.HttpContext.Response.Headers.RetryAfter = SocketLimits.WaitSeconds(lease).ToString();
        return Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests",
            detail: "Too many requests. Wait before trying again.");
    }
}

public static class Routes
{
    public const int PublicPort = 8080;
    public const int ReportPort = 8081;

    /// <summary>Only the loopback binding reaches the report port, so the LAN gets the status and never the topology.</summary>
    public static void MapHealth(WebApplication app, Func<Report?> latest, int reportPort, ProbeLimits limits)
    {
        app.MapGet("/alive", () => Results.Ok()).AddEndpointFilter(limits);
        app.MapGet("/health", (HttpContext context) =>
        {
            var report = latest();
            if (report is null) return Results.Json(new { healthy = false, error = "first check still running" }, statusCode: 503);
            var healthy = report.Healthy && !Bounded.IsStale(report.CheckedAt, DateTimeOffset.UtcNow);
            var status = healthy ? 200 : 503;
            return context.Connection.LocalPort == reportPort
                ? Results.Json(report with { Healthy = healthy }, statusCode: status)
                : Results.Json(new { healthy, report.CheckedAt }, statusCode: status);
        }).AddEndpointFilter(limits);
    }
}
