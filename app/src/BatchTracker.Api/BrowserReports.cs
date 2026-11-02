using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace BatchTracker.Api;

/// <summary>What a browser sends about a failure. All of it is caller text.</summary>
public sealed record BrowserReport(string? Id, string? Kind, string? Route, int? Status, string? CorrelationId, string? Message);

/// <summary>Turns a browser failure into a service log entry, so one query shows it beside the rows of its request.</summary>
public static class BrowserReports
{
    public const int MaxBody = 8 * 1024;
    public const int MaxRoute = 200;

    private static readonly string[] Kinds = ["console-error", "socket-failure", "request-failure", "render-fault"];

    /// <summary>Bound here and not by the route: the framework's own refusals are not anonymous, so they would answer 401.</summary>
    public static void MapBrowserReports(this RouteGroupBuilder api) =>
        api.MapPost("/browser-reports", async (HttpRequest request, ILogSink sink, IConfiguration configuration, TimeProvider time, CancellationToken token) =>
            {
                if (request.ContentLength > MaxBody) return TooLarge();
                if (!request.HasJsonContentType()) return Bad("The body must be JSON.");

                BrowserReport? report;
                try
                {
                    report = await request.ReadFromJsonAsync<BrowserReport>(token);
                }
                catch (JsonException)
                {
                    return Bad("The body is not a report.");
                }
                catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return TooLarge();
                }

                if (report is null) return Bad("The body is not a report.");
                if (Problem(report) is { } problem) return Bad(problem);

                sink.Add(Entry(report, configuration.PodName(), time.GetUtcNow()));
                return Results.Accepted(value: new { report.Id });
            })
            .AllowAnonymous()
            .LimitedAsBrowserReport()
            .WithMetadata(new RequestSizeLimitAttribute(MaxBody));

    /// <summary>Null means valid.</summary>
    public static string? Problem(BrowserReport report)
    {
        if (!Guid.TryParseExact(report.Id, "D", out _)) return "id must be a UUID.";
        if (report.Kind is null || !Kinds.Contains(report.Kind)) return $"kind must be one of {string.Join(", ", Kinds)}.";
        if (report.Route is null || !report.Route.StartsWith('/') || report.Route.StartsWith("//")) return "route must be a page path.";
        if (report.Status is < 100 or > 599) return "status must be 100 to 599.";
        if (report.CorrelationId is not null && !Correlation.IsValid(report.CorrelationId)) return "correlationId is malformed.";
        return null;
    }

    /// <summary>The report's id is stored, never put in force: caller text must not reach baggage, which goes out on every call.</summary>
    public static ServiceLogEntry Entry(BrowserReport report, string pod, DateTimeOffset at) => new(
        at, ServiceLogLevel.Error, ServiceLogLevel.BrowserReason,
        Secrets.MaskAndCut(string.IsNullOrWhiteSpace(report.Message) ? $"Browser failure: {report.Kind}" : report.Message, ServiceLogProvider.MaxMessage),
        ServiceLogLevel.BrowserService, pod, report.CorrelationId, null, null,
        HttpStatus: report.Status, ReportId: report.Id, Kind: report.Kind, Route: Secrets.MaskAndCut(PathOf(report.Route!), MaxRoute));

    /// <summary>A query or fragment can hold a token, so neither is kept.</summary>
    private static string PathOf(string route)
    {
        var end = route.IndexOfAny(['?', '#']);
        return end < 0 ? route : route[..end];
    }

    private static IResult Bad(string detail) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: detail);

    private static IResult TooLarge() =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, detail: $"A report is limited to {MaxBody} bytes.");
}
