using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.Line;

/// <summary>The routes of the Line, in the order they are mapped.</summary>
public static class Endpoints
{
    /// <summary>Every route but readiness.</summary>
    public static void MapLine(this WebApplication app)
    {
        app.MapControl();
        app.MapLogRead();
        app.MapGet("/line/ws", LineFeed.RunAsync).RequireAuthorization(Permissions.OpsAdmin);
        app.MapGet("/healthz", () => Results.Ok()).AllowAnonymous().LimitedAsHealth();
    }

    /// <summary>Start, stop and status of the line.</summary>
    private static void MapControl(this WebApplication app)
    {
        app.MapPost("/line", (RateRequest? body, LineEngine engine) =>
        {
            var rate = body?.Rate ?? LineEngine.DefaultRate;
            if (!double.IsFinite(rate) || rate < 0 || rate > LineEngine.MaxRate)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: $"rate must be 0 to {LineEngine.MaxRate}.");
            engine.Start(rate);
            return Results.Ok(engine.Status());
        }).RequireAuthorization(Permissions.OpsAdmin);

        app.MapDelete("/line", (LineEngine engine) =>
        {
            engine.Stop();
            return Results.Ok(engine.Status());
        }).RequireAuthorization(Permissions.OpsAdmin);

        app.MapGet("/line", (LineEngine engine) => Results.Ok(engine.Status())).RequireAuthorization(Permissions.OpsAdmin);

        // Any signed-in caller: the monitor holds no permission, and these are figures only.
        app.MapGet("/line/bundles", (LineEngine engine) => Results.Ok(engine.Bundles())).RequireAuthorization();
    }

    /// <summary>The line log as an admin reads it.</summary>
    private static void MapLogRead(this WebApplication app)
    {
        app.MapGet("/line/logs", (
            string? level, string? reason, string? farm, string? batch,
            DateTimeOffset? from, DateTimeOffset? to, int? limit, string? correlationId, LineLog log) =>
        {
            var query = new LogQuery(level, reason, farm, batch, from, to, limit, correlationId);
            return LineLog.Problem(query) is { } problem
                ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: problem)
                : Results.Ok(log.Query(query));
        }).RequireAuthorization(Permissions.OpsAdmin);
    }
}
