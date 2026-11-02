using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.Live;

/// <summary>The routes of Live, in the order they are mapped.</summary>
public static class Endpoints
{
    /// <summary>Every route but readiness.</summary>
    public static void MapLive(this WebApplication app)
    {
        app.MapReports();
        app.MapLogRead();
        app.MapGet("/ws/hops", HopSocket.RunAsync).RequireAuthorization();
        app.MapGet("/healthz", () => Results.Ok()).AllowAnonymous().LimitedAsHealth();
    }

    /// <summary>What the other services post. Each batch step posts here, so the Line's top rate would turn a line per report into the bulk of the log.</summary>
    private static void MapReports(this WebApplication app)
    {
        // Reached only inside the cluster: the ingress forwards /ws and /live/logs, never /reports, /hops, /changes or /logs.
        app.MapPost("/reports", Receive).RequireAuthorization(AuthSetup.InternalPolicy).LimitedAsMachine().WithoutRequestLine();

        app.MapPost("/hops", (Hop hop, HopHub hub) =>
        {
            hub.Add(hop);
            return Results.Accepted();
        }).RequireAuthorization(AuthSetup.InternalPolicy).LimitedAsMachine().WithoutRequestLine();

        app.MapPost("/changes", (Versioned row, HopHub hub) =>
        {
            hub.Apply(row);
            return Results.Accepted();
        }).RequireAuthorization(AuthSetup.InternalPolicy).LimitedAsMachine().WithoutRequestLine();

        app.MapPost("/logs", (ServiceLogEntry entry, ServiceLog log) =>
        {
            if (ServiceLog.Problem(entry) is { } problem)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: problem);
            log.Add(entry);
            return Results.Accepted();
        }).RequireAuthorization(AuthSetup.InternalPolicy).LimitedAsMachine().WithoutRequestLine();
    }

    /// <summary>A bad log entry is skipped and not a refused post: it would take the pod's hops down with it.</summary>
    private static IResult Receive(LiveReports reports, HopHub hub, ServiceLog log)
    {
        foreach (var hop in reports.Hops) hub.Add(hop);
        foreach (var row in reports.Changes) hub.Apply(row);
        log.AddPost(reports.PostId, reports.Logs.Where(e => ServiceLog.Problem(e) is null));
        return Results.Accepted();
    }

    /// <summary>The service log as an admin reads it.</summary>
    private static void MapLogRead(this WebApplication app)
    {
        app.MapGet("/live/logs", (
            string? level, string? reason, string? farm, string? batch, string? service, string? correlationId,
            DateTimeOffset? from, DateTimeOffset? to, int? limit, string? reportId, ServiceLog log) =>
        {
            var query = new ServiceLogQuery(level, service, correlationId, from, to, limit, reason, farm, batch, reportId);
            return ServiceLog.Problem(query) is { } problem
                ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: problem)
                : Results.Ok(log.Query(query));
        }).RequireAuthorization(Permissions.OpsAdmin);
    }
}
