using System.Security.Claims;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.Api;

public sealed record BundleRequest(IReadOnlyList<BundleItem>? Items);
public sealed record BundleResult(int Status, string CorrelationId, string? Etag, Batch? Batch, string? Detail);
public sealed record BundleReply(IReadOnlyList<BundleResult> Results);

/// <summary>The route that takes many steps in one request, each answered as its single route would.</summary>
public static class BundleEndpoints
{
    /// <summary>An item failing is its result, so the reply is 200 unless the list itself is refused.</summary>
    public static void MapBundle(this RouteGroupBuilder api) =>
        api.MapPost("/batches/bundle", async (
            BundleRequest body, ClaimsPrincipal user, BatchBundle bundle, ILogger<BatchBundle> logger, CancellationToken token) =>
        {
            if (body.Items is null) return Endpoints.Problem(StatusCodes.Status400BadRequest, "items is required.");
            if (body.Items.Count > BatchBundle.MaxItems) return Endpoints.Problem(StatusCodes.Status400BadRequest, $"items is limited to {BatchBundle.MaxItems}.");

            var answers = await bundle.RunAsync(user.ToCaller(), body.Items, token);
            var results = answers.Select((answer, i) => ResultOf(body.Items[i], answer, logger)).ToList();
            return Results.Ok(new BundleReply(results));
        }).RequireAuthorization(Permissions.BatchWrite);

    private static BundleResult ResultOf(BundleItem item, BundleAnswer answer, ILogger logger)
    {
        using var action = Correlation.Use(answer.CorrelationId);
        logger.LogInformation("Request POST bundle {Kind}", item.Kind);
        if (answer.Outcome.Value is not { } row)
            return new BundleResult(Endpoints.StatusOf(answer.Outcome.Failure!.Value), answer.CorrelationId, null, null, answer.Outcome.Detail);
        var status = item.Kind == BundleKinds.Create ? StatusCodes.Status201Created : StatusCodes.Status200OK;
        return new BundleResult(status, answer.CorrelationId, row.ETag, row.Batch, null);
    }
}
