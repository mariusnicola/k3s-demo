using System.Security.Claims;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.Api;

public sealed record LoginRequest(string? User);

/// <summary>The routes that start, end and read a session.</summary>
public static class AuthEndpoints
{
    public static void MapAuth(this RouteGroupBuilder api)
    {
        api.MapPost("/auth/dev-login", DevLoginAsync).AllowAnonymous().CountsAsSignIn();

        api.MapPost("/auth/logout", (SessionSettings session, HttpResponse response) =>
        {
            SessionCookie.Clear(response, session);
            return Results.NoContent();
        }).RequireAuthorization().CountsOnlyACredential();

        // Services keep a bearer token: they have no cookie jar and no Origin to check.
        api.MapPost("/auth/machine", (HttpRequest request, TokenIssuer issuer, IConfiguration configuration) =>
            MachineCredential.CallerFor(request.Headers[MachineCredential.Header].ToString(), configuration) is { } machine
                ? Results.Ok(new { token = issuer.Issue(machine) })
                : Endpoints.Problem(StatusCodes.Status401Unauthorized, "Unknown machine.")).AllowAnonymous().CountsAsSignIn();

        api.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(BodyOf(user.ToCaller()))).RequireAuthorization().CountsOnlyACredential();

        // Open, so a signed-out page load is an answer and not a failed request, and an open route never reaches the failure count.
        api.MapGet("/session", (ClaimsPrincipal user) =>
            Results.Ok(new { me = user.Identity?.IsAuthenticated == true ? BodyOf(user.ToCaller()) : null })).AllowAnonymous();
    }

    private static object BodyOf(Caller caller) => new { caller.User, caller.Permissions, caller.Farms };

    private static async Task<IResult> DevLoginAsync(
        HttpRequest request, TokenIssuer issuer, IConfiguration configuration, SessionSettings session, CancellationToken token)
    {
        if (!DevLoginEnabled(configuration)) return Results.NotFound();

        // Bound here and not by the route: the framework's 415 route is not anonymous, so it would answer 401.
        if (!request.HasJsonContentType())
            return Endpoints.Problem(StatusCodes.Status415UnsupportedMediaType, "Send JSON.");
        var body = await ReadLoginAsync(request, token);
        if (string.IsNullOrWhiteSpace(body?.User)) return Endpoints.Problem(StatusCodes.Status400BadRequest, "user is required.");

        var caller = DemoUsers.Find(body.User);
        if (caller is null) return Endpoints.Problem(StatusCodes.Status401Unauthorized, "Unknown user.");
        SessionCookie.Write(request.HttpContext.Response, issuer.Issue(caller), session);
        return Results.Ok(BodyOf(caller));
    }

    /// <summary>Off unless set to true, so a build deployed without the setting has no open sign-in.</summary>
    private static bool DevLoginEnabled(IConfiguration configuration) =>
        bool.TryParse(configuration["DEV_LOGIN_ENABLED"], out var enabled) && enabled;

    private static async Task<LoginRequest?> ReadLoginAsync(HttpRequest request, CancellationToken token)
    {
        try
        {
            return await request.ReadFromJsonAsync<LoginRequest>(token);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
