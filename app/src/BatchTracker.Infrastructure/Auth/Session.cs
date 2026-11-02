using System.Security.Cryptography;
using System.Text;
using BatchTracker.Application;
using BatchTracker.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BatchTracker.Infrastructure;

public sealed record SessionSettings(bool SecureCookie, IReadOnlySet<string> AllowedOrigins)
{
    public static SessionSettings From(IConfiguration configuration) => new(
        // Anything but an explicit false stays Secure, so a missing setting fails closed.
        !bool.TryParse(configuration["COOKIE_SECURE"], out var secure) || secure,
        configuration.Require("ALLOWED_ORIGINS")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(o => o.TrimEnd('/'))
            .ToHashSet(StringComparer.Ordinal));
}

public static class MachineCredential
{
    public const string Header = "X-Machine-Token";
    public const string Setting = "LINE_MACHINE_TOKEN";
    public const string MonitorSetting = "MONITOR_MACHINE_TOKEN";

    public static bool Matches(string sent, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(expected));

    /// <summary>Both tokens are compared on every call, so the time taken does not say which one was close.</summary>
    public static Caller? CallerFor(string sent, IConfiguration configuration)
    {
        var line = Matches(sent, configuration.Require(Setting));
        var monitor = Matches(sent, configuration.Require(MonitorSetting));
        return line ? DemoUsers.LineMachine : monitor ? DemoUsers.MonitorMachine : null;
    }
}

public static class SessionCookie
{
    public const string Name = "bt_session";

    public static void Write(HttpResponse response, string token, SessionSettings options) =>
        response.Cookies.Append(Name, token, Attributes(options, TokenIssuer.Lifetime));

    public static void Clear(HttpResponse response, SessionSettings options) =>
        response.Cookies.Delete(Name, Attributes(options, null));

    private static CookieOptions Attributes(SessionSettings options, TimeSpan? lifetime) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = options.SecureCookie,
        Path = "/",
        MaxAge = lifetime
    };
}

/// <summary>SameSite treats another port on the same host as the same site, so the browser's own check is not enough.</summary>
public static class OriginRule
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    /// <summary>A request with no Origin is a script unless it carries the cookie a browser would have attached.</summary>
    public static bool Allows(HttpRequest request, SessionSettings options)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0) return options.AllowedOrigins.Contains(origin);
        return !request.Cookies.ContainsKey(SessionCookie.Name) || request.Headers.ContainsKey("Authorization");
    }

    public static IApplicationBuilder UseOriginGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<SessionSettings>();
            if (SafeMethods.Contains(context.Request.Method) || Allows(context.Request, options))
            {
                await next(context);
                return;
            }
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Origin not allowed.").ExecuteAsync(context);
        });
}
