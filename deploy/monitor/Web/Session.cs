using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

/// <summary>Holds only the public key, so the monitor can check a session and cannot mint one.</summary>
public sealed class SessionReader
{
    public const string Issuer = "batch-tracker";
    public const string PermissionClaim = "perm";
    public const string OpsAdmin = "ops.admin";
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private const int CurveBits = 256;

    private readonly JsonWebTokenHandler handler = new();
    private readonly TokenValidationParameters rules;

    public SessionReader(string publicKey) => rules = new TokenValidationParameters
    {
        ValidIssuer = Issuer,
        ValidAudience = Issuer,
        IssuerSigningKey = Load(publicKey),
        ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        ClockSkew = ClockSkew
    };

    /// <summary>When the session ends, or null for anyone who may not read the report.</summary>
    public async Task<DateTimeOffset?> AdminUntil(HttpRequest request)
    {
        // A browser always sends Origin on a socket, so a cookie without one is a script holding a copied session.
        return request.Headers.Origin.Count == 0 ? null : await CookieAdminUntil(request);
    }

    /// <summary>The same check for a plain GET: a browser sends no Origin on a same-origin GET, and SameSite keeps the cookie off a cross-site one.</summary>
    public async Task<DateTimeOffset?> CookieAdminUntil(HttpRequest request)
    {
        if (request.Cookies[BrowserPaths.SessionCookie] is not { Length: > 0 } token) return null;

        var result = await handler.ValidateTokenAsync(token, rules);
        if (!result.IsValid || result.ClaimsIdentity is not { } identity) return null;
        if (identity.FindFirst(JwtRegisteredClaimNames.Sub) is not { Value.Length: > 0 }) return null;
        if (!identity.HasClaim(PermissionClaim, OpsAdmin)) return null;
        // The skew forgives a clock, not a session that already ended: a 4401 here would only bring a reconnect and another 4401.
        var until = new DateTimeOffset(result.SecurityToken.ValidTo, TimeSpan.Zero);
        return until > DateTimeOffset.UtcNow ? until : null;
    }

    private static ECDsaSecurityKey Load(string base64)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64), out _);
        }
        catch (Exception problem) when (problem is FormatException or CryptographicException)
        {
            throw new InvalidOperationException("JWT_PUBLIC_KEY does not load");
        }
        if (key.KeySize != CurveBits) throw new InvalidOperationException("JWT_PUBLIC_KEY is not P-256");
        return new ECDsaSecurityKey(key);
    }
}

public static class MonitorSocket
{
    /// <summary>Origin is checked by the WebSocket middleware. The session only decides which frames this socket gets.</summary>
    public static void Use(
        WebApplication app, string ingressOrigin, SessionReader sessions, SocketLimits limits, SocketHub hub, Func<Report?> latest, ChangeFeed feed, Operations operations)
    {
        var sockets = new WebSocketOptions();
        sockets.AllowedOrigins.Add(ingressOrigin);
        app.UseWebSockets(sockets);
        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            var client = limits.Client(context);
            if (limits.Refuse(client) is { } wait)
            {
                await TooMany(context, wait);
                return;
            }
            try
            {
                await Accept(context, sessions, hub, latest, feed, operations);
            }
            finally
            {
                limits.Leave(client);
            }
        });
    }

    private static async Task TooMany(HttpContext context, int wait)
    {
        context.Response.Headers.RetryAfter = wait.ToString();
        await Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests",
            detail: "Too many sockets. Wait before trying again.").ExecuteAsync(context);
    }

    private static async Task Accept(
        HttpContext context, SessionReader sessions, SocketHub hub, Func<Report?> latest, ChangeFeed feed, Operations operations)
    {
        var adminUntil = await sessions.AdminUntil(context.Request);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        object?[] first = adminUntil is null ? [operations.Frame()] : [latest(), feed.Frame(), operations.Frame()];
        await hub.Run(socket, first.OfType<object>(), adminUntil, context.RequestAborted);
    }
}
