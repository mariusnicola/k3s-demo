using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BatchTracker.Infrastructure;

/// <summary>Fixed plain words for a problem body. No text here comes from an exception, so none can leak one.</summary>
public static class ProblemWords
{
    public const string NoBatch = "No batch was affected.";
    public const string OnlyRead = "Nothing changed: this was only a read.";
    public const string NotChanged = "The batch was not changed.";
    public const string MayHaveChanged = "The batch may or may not have changed. Reload it to see.";
    public const string FlagsMayHaveChanged = "Some batches may have been flagged. Reload the list to see.";
    public const string SignInOff = "Signing in with a demo user is off on this server.";
    public const string SignInOffNext = "Ask an admin how to sign in here.";

    private const string DevLoginPath = "/api/auth/dev-login";

    private static readonly string[] BatchPaths = ["/api/batches", "/events"];
    private static readonly string[] FlagPaths = ["/api/compliance/run", "/nightly/run"];

    private static readonly (string Happened, string Next) Fallback =
        ("The request could not be completed.", "Try again. If it repeats, give support the correlation ID.");

    private static readonly Dictionary<int, (string Happened, string Next)> ByStatus = new()
    {
        [StatusCodes.Status400BadRequest] = ("The request held a value the server cannot use.", "Correct the value and send it again."),
        [StatusCodes.Status401Unauthorized] = ("The request came without a valid session.", "Sign in, then try again."),
        [StatusCodes.Status403Forbidden] = ("You do not have the permission or the farm for this.", "Ask an admin for access."),
        [StatusCodes.Status404NotFound] = ("That item does not exist, or it is on a farm you cannot reach.", "Check the link, or go back to the list."),
        [StatusCodes.Status405MethodNotAllowed] = ("This address does not take that kind of request.", "Use the page or the documented call."),
        [StatusCodes.Status409Conflict] = ("The batch is not in a state that allows this.", "Reload the batch, then take the step it allows."),
        [StatusCodes.Status412PreconditionFailed] = ("The batch changed after you read it.", "Reload the batch, then repeat your step."),
        [StatusCodes.Status415UnsupportedMediaType] = ("The request was not sent as JSON.", "Send the request as JSON."),
        [StatusCodes.Status422UnprocessableEntity] = ("This request key was used before with a different body.", "Use a new key for a new request."),
        [StatusCodes.Status428PreconditionRequired] = ("The request did not say which version of the batch it read.", "Reload the batch, then repeat your step."),
        [StatusCodes.Status429TooManyRequests] = ("Too many requests arrived from this client.", "Wait a moment, then try again."),
        [StatusCodes.Status500InternalServerError] = ("The server hit an unexpected error and could not finish.", "Try again. If it repeats, give support the correlation ID."),
        [StatusCodes.Status503ServiceUnavailable] = ("A service this needs is not available right now.", "Try again in a moment.")
    };

    public static IReadOnlyCollection<int> Statuses => ByStatus.Keys;

    /// <summary>What happened at this status, in plain words.</summary>
    public static string HappenedFor(int status) => ByStatus.GetValueOrDefault(status, Fallback).Happened;

    /// <summary>What to do next at this status, in plain words.</summary>
    public static string NextFor(int status) => ByStatus.GetValueOrDefault(status, Fallback).Next;

    /// <summary>What the failed request did to a batch, judged from the route, the method and the status alone.</summary>
    public static string ImpactOf(string method, string path, int status)
    {
        if (FlagPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return status >= StatusCodes.Status500InternalServerError ? FlagsMayHaveChanged : NoBatch;
        if (!BatchPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return NoBatch;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) return OnlyRead;
        return status >= StatusCodes.Status500InternalServerError ? MayHaveChanged : NotChanged;
    }

    /// <summary>The wait comes from the header value, so the words and the header can never disagree.</summary>
    private static string NextOf(int status, HttpContext context)
    {
        if (status != StatusCodes.Status429TooManyRequests || !int.TryParse(context.Response.Headers.RetryAfter, out var seconds))
            return NextFor(status);
        return $"Wait {seconds} {(seconds == 1 ? "second" : "seconds")}, then try again.";
    }

    /// <summary>Keeps a route's own detail, except on a 500, whose text could only come from an exception.</summary>
    public static void Apply(ProblemDetails problem, HttpContext context)
    {
        var status = problem.Status ?? context.Response.StatusCode;
        var signInOff = status == StatusCodes.Status404NotFound && context.Request.Path.Equals(DevLoginPath, StringComparison.OrdinalIgnoreCase);
        if (problem.Detail is null || status >= StatusCodes.Status500InternalServerError) problem.Detail = signInOff ? SignInOff : HappenedFor(status);
        problem.Extensions["where"] = ProblemPlaces.Of(context.Request.Method, context.Request.Path);
        problem.Extensions["path"] = $"{context.Request.Method} {context.Request.Path}";
        problem.Extensions["impact"] = ImpactOf(context.Request.Method, context.Request.Path, status);
        problem.Extensions["next"] = signInOff ? SignInOffNext : NextOf(status, context);
    }
}
