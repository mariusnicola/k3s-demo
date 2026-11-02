using BatchTracker.Application;
using BatchTracker.Infrastructure;

namespace BatchTracker.Worker;

/// <summary>Lets test hosts find this assembly's entry point.</summary>
public sealed class WorkerMarker;

public static class Endpoints
{
    public static void MapWorker(this WebApplication app)
    {
        app.MapPost("/events", async (BatchEvent message, EventRecorder recorder, CancellationToken token) =>
            await recorder.HandleAsync(message, token) switch
            {
                RecordResult.Done => Results.Ok(),
                RecordResult.Refused => Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity, detail: "Event refused."),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, detail: "Batch kept changing.")
            }).AddEndpointFilter<WorkInFlight>().RequireAuthorization(AuthSetup.InternalPolicy).LimitedAsMachine();

        app.MapPost("/nightly/run", async (ComplianceRun run, CancellationToken token) =>
            Results.Ok(await run.RunAsync(token))).AddEndpointFilter<WorkInFlight>().RequireAuthorization(AuthSetup.InternalPolicy).LimitedAsMachine();

        app.MapGet("/ready", async (IBatchStore store, CancellationToken token) =>
        {
            // Under the Dispatcher's 4 s limit on this call, so it gets an answer and not a cut.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            return await store.ReachableAsync(limit.Token) ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        })
            .RequireAuthorization(AuthSetup.InternalPolicy).LimitedAsHealth();

        app.MapGet("/healthz", () => Results.Ok()).AllowAnonymous().LimitedAsHealth();
    }
}
