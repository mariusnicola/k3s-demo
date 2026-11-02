using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BatchTracker.Infrastructure;

/// <summary>Marks a route that is open by design and still spends a failure on a refused credential.</summary>
public sealed class SignInRoute;

/// <summary>Marks a route the app calls with or without a session, so a request with no credential guesses nothing.</summary>
public sealed class NoGuessWithoutCredential;

/// <summary>Holds each refused credential a little longer, and turns away a client that already has too many waiting.</summary>
public sealed class FailureDelayMiddleware(RequestDelegate next, FailureLedger ledger, TimeProvider time)
{
    /// <summary>Who gives a seat back: the end of the request, unless a delay's timer took that over.</summary>
    private sealed class Seat
    {
        public bool FreedByTimer;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!Counts(context) || AsksWithoutGuessing(context) || await IsOurOwnServiceAsync(context))
        {
            await next(context);
            return;
        }

        var client = ClientAddress.Of(context);
        var refused = ledger.IsRefused(client, out var left);
        var signIn = context.GetEndpoint()?.Metadata.GetMetadata<SignInRoute>() is not null;
        if (refused && !signIn)
        {
            await RefuseAsync(context, left);
            return;
        }
        if (!ledger.TryEnter(client, out var wait))
        {
            await RefuseAsync(context, wait);
            return;
        }

        var seat = WatchSeat(context, client);
        if (refused) await CheckCredentialFirstAsync(context, client, seat);
        else await next(context);
    }

    /// <summary>The hold sits on the first byte, so it covers an empty challenge and a route's own 401 alike.</summary>
    private Seat WatchSeat(HttpContext context, string client)
    {
        var seat = new Seat();
        context.Response.OnStarting(() => SettleAsync(context, client, seat));
        context.Response.OnCompleted(() =>
        {
            if (!seat.FreedByTimer) ledger.Leave(client);
            return Task.CompletedTask;
        });
        return seat;
    }

    /// <summary>A right credential that gets a seat passes at once, so the route runs held back. A wrong one holds its seat for the delay, then gets the refusal.</summary>
    private async Task CheckCredentialFirstAsync(HttpContext context, string client, Seat seat)
    {
        var body = context.Response.Body;
        using var held = new MemoryStream();
        context.Response.Body = held;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = body;
        }

        if (context.Response.StatusCode != StatusCodes.Status401Unauthorized)
        {
            held.Position = 0;
            await held.CopyToAsync(body, context.RequestAborted);
            return;
        }
        var delay = ledger.Fail(client);

        // Cleared first: a client that leaves during the hold must not reach SettleAsync with its 401 and count twice.
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await HoldAsync(context, client, seat, delay);
        if (context.RequestAborted.IsCancellationRequested) return;
        ledger.IsRefused(client, out var left);
        await RefuseAsync(context, left);
    }

    private static async Task RefuseAsync(HttpContext context, TimeSpan wait)
    {
        context.Response.Headers.RetryAfter = RateLimits.WholeSeconds(wait).ToString(CultureInfo.InvariantCulture);
        await Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests",
            detail: "Too many failed requests. Wait before trying again.").ExecuteAsync(context);
    }

    /// <summary>Authentication ahead of this step only tries the default scheme, so a report's token has to be read here.</summary>
    private static async Task<bool> IsOurOwnServiceAsync(HttpContext context)
    {
        var rules = context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>() ?? [];
        return rules.Any(r => r.Policy == AuthSetup.InternalPolicy)
            && (await context.AuthenticateAsync(AuthSetup.InternalPolicy)).Succeeded;
    }

    /// <summary>Only an empty request passes: a refused credential on this route is a guess like any other.</summary>
    private static bool AsksWithoutGuessing(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<NoGuessWithoutCredential>() is not null
        && !context.Request.Headers.ContainsKey("Authorization")
        && !context.Request.Cookies.ContainsKey(SessionCookie.Name);

    /// <summary>Refused credentials count on protected routes and on sign-in routes.</summary>
    private static bool Counts(HttpContext context)
    {
        if (context.GetEndpoint() is not { } endpoint) return false;
        if (context.User.Identity?.IsAuthenticated == true) return false;
        return endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null || endpoint.Metadata.GetMetadata<SignInRoute>() is not null;
    }

    private async Task SettleAsync(HttpContext context, string client, Seat seat)
    {
        if (context.Response.StatusCode != StatusCodes.Status401Unauthorized) return;

        await HoldAsync(context, client, seat, ledger.Fail(client));
    }

    /// <summary>Holds no thread: a timer frees the seat when the delay ends, so a client that leaves early only stops waiting for the answer.</summary>
    private async Task HoldAsync(HttpContext context, string client, Seat seat, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        seat.FreedByTimer = true;
        ledger.LeaveAfter(client, delay);
        try
        {
            await Task.Delay(delay, time, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // The client left, so the count and the seat stand and nobody is waiting for the answer.
        }
    }
}

/// <summary>Wires the delay into a service and marks its routes.</summary>
public static class FailureDelaySetup
{
    public static IServiceCollection AddFailureDelays(this IServiceCollection services)
    {
        services.AddSingleton(new FailureRules());
        services.AddSingleton(s => TrustedProxies.From(s.GetRequiredService<IConfiguration>()["TRUSTED_PROXIES"]));
        return services.AddSingleton(s => new FailureLedger(s.GetRequiredService<FailureRules>(), s.GetRequiredService<TimeProvider>()));
    }

    /// <summary>Reads the proxy list here, so a bad setting stops the start and not the first request.</summary>
    public static IApplicationBuilder UseFailureDelays(this IApplicationBuilder app)
    {
        app.ApplicationServices.GetRequiredService<TrustedProxies>();
        return app.UseMiddleware<FailureDelayMiddleware>();
    }

    /// <summary>Spends no failure when the request carries no credential.</summary>
    public static TBuilder CountsOnlyACredential<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new NoGuessWithoutCredential());

    public static TBuilder CountsAsSignIn<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new SignInRoute());
}
