using System.Security.Claims;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace BatchTracker.Api;

/// <summary>Lets test hosts find this assembly's entry point.</summary>
public sealed class ApiMarker;

public sealed record CreateRequest(string? FarmId, string? Product);
public sealed record AdvanceRequest(string? To);
public sealed record InspectRequest(bool? Passed, string? Reason);

public static class Endpoints
{
    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapAuth();
        MapBatchReads(api);
        MapBatchChanges(api);
        api.MapBundle();
        MapBatchRepairs(api);
        MapReports(api);
        MapHealth(api);
        MapHealthz(app);
        api.MapBrowserReports();
    }

    private static void MapBatchReads(RouteGroupBuilder api)
    {
        api.MapGet("/batches", async (
            ClaimsPrincipal user, BatchService service, string? state, string? farmId, int? limit, CancellationToken token) =>
        {
            var outcome = await service.ListAsync(user.ToCaller(), state, farmId, limit, token);
            return outcome.Value is null ? Problem(outcome.Failure!.Value, outcome.Detail!) : Results.Ok(outcome.Value);
        }).RequireAuthorization(Permissions.BatchRead);

        api.MapGet("/batches/{farmId}/{id}", async (
            string farmId, string id, ClaimsPrincipal user, BatchService service, HttpResponse response, CancellationToken token) =>
            Reply(await service.GetAsync(user.ToCaller(), farmId, id, token), response))
            .RequireAuthorization(Permissions.BatchRead);
    }

    private static void MapBatchChanges(RouteGroupBuilder api)
    {
        api.MapPost("/batches", async (
            CreateRequest body, ClaimsPrincipal user, BatchService service, HttpRequest request, HttpResponse response, CancellationToken token) =>
        {
            var key = request.Headers[BatchService.IdempotencyHeader].ToString();
            var outcome = await service.CreateAsync(user.ToCaller(), body.FarmId, body.Product, key, token);
            if (outcome.Value is not { } row) return Problem(outcome.Failure!.Value, outcome.Detail!);
            response.Headers.ETag = row.ETag;
            return Results.Created($"/api/batches/{row.Batch.FarmId}/{row.Batch.Id}", row.Batch);
        }).RequireAuthorization(Permissions.BatchWrite);

        api.MapPost("/batches/{farmId}/{id}/advance", async (
            string farmId, string id, AdvanceRequest body, ClaimsPrincipal user, BatchService service,
            HttpContext context, CancellationToken token) =>
            Reply(await service.AdvanceAsync(user.ToCaller(), farmId, id, body.To, IfMatch(context), token), context.Response))
            .RequireAuthorization(Permissions.BatchWrite);

        api.MapPost("/batches/{farmId}/{id}/inspect", async (
            string farmId, string id, InspectRequest body, ClaimsPrincipal user, BatchService service,
            HttpContext context, CancellationToken token) =>
            Reply(await service.InspectAsync(
                user.ToCaller(), farmId, id, body.Passed, body.Reason, IfMatch(context), token), context.Response))
            .RequireAuthorization(Permissions.BatchInspect);
    }

    private static void MapBatchRepairs(RouteGroupBuilder api)
    {
        api.MapPost("/batches/{farmId}/{id}/fail-next", async (
            string farmId, string id, ClaimsPrincipal user, BatchService service, HttpResponse response, CancellationToken token) =>
            Reply(await service.FailNextAsync(user.ToCaller(), farmId, id, token), response))
            .RequireAuthorization(Permissions.OpsAdmin);

        api.MapPost("/batches/{farmId}/{id}/replay", async (
            string farmId, string id, ClaimsPrincipal user, BatchService service, HttpResponse response, CancellationToken token) =>
            Reply(await service.ReplayAsync(user.ToCaller(), farmId, id, token), response))
            .RequireAuthorization(Permissions.OpsAdmin);
    }

    private static void MapReports(RouteGroupBuilder api)
    {
        api.MapGet("/summary", async (ClaimsPrincipal user, BatchService service, CancellationToken token) =>
            Results.Ok(await service.SummaryAsync(user.ToCaller(), token)))
            .RequireAuthorization(Permissions.BatchRead);

        api.MapGet("/alerts", async (ClaimsPrincipal user, BatchService service, CancellationToken token) =>
            Results.Ok(await service.AlertsAsync(user.ToCaller(), token)))
            .RequireAuthorization(Permissions.BatchRead);

        api.MapPost("/compliance/run", async (IWorkerClient worker, CancellationToken token) =>
            Results.Ok(await worker.RunNightlyAsync(token)))
            .RequireAuthorization(Permissions.OpsAdmin);
    }

    private static void MapHealth(RouteGroupBuilder api)
    {
        api.MapGet("/ready", async (StoreWatch store, ShutdownState state, CancellationToken token) =>
            !state.Draining && await store.GoodAsync(token) ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous().LimitedAsHealth();

        api.MapGet("/ready/deep", async (StoreWatch store, IBusProbe bus, CancellationToken token) =>
        {
            var db = await store.GoodAsync(token);
            var serviceBus = await bus.ReachableAsync(token);
            return Results.Json(
                new { db, dbMs = store.LastReadMs, serviceBus },
                statusCode: db && serviceBus ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous().LimitedAsDeepReady();
    }

    private static void MapHealthz(WebApplication app)
    {
        foreach (var path in new[] { "/healthz", "/api/healthz" })
            app.MapGet(path, () => Results.Ok()).AllowAnonymous().LimitedAsHealth();
    }

    private static string? IfMatch(HttpContext context) => context.Request.Headers.IfMatch.FirstOrDefault();

    private static IResult Reply(Outcome<Versioned> outcome, HttpResponse response)
    {
        if (outcome.Value is not { } row) return Problem(outcome.Failure!.Value, outcome.Detail!);
        response.Headers.ETag = row.ETag;
        return Results.Ok(row.Batch);
    }

    private static IResult Problem(Failure failure, string detail) => Problem(StatusOf(failure), detail);

    internal static int StatusOf(Failure failure) => failure switch
    {
        Failure.Validation => StatusCodes.Status400BadRequest,
        Failure.Forbidden => StatusCodes.Status403Forbidden,
        Failure.NotFound => StatusCodes.Status404NotFound,
        Failure.Conflict => StatusCodes.Status409Conflict,
        Failure.Stale => StatusCodes.Status412PreconditionFailed,
        Failure.MissingIfMatch => StatusCodes.Status428PreconditionRequired,
        Failure.KeyReused => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError
    };

    internal static IResult Problem(int status, string detail) => Results.Problem(new ProblemDetails
    {
        Status = status,
        Detail = detail
    });
}
