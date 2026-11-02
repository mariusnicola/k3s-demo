using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BatchTracker.Infrastructure;

public sealed class InternalTokenOptions : AuthenticationSchemeOptions
{
    public string Token { get; set; } = "";
}

public sealed class InternalTokenHandler(
    IOptionsMonitor<InternalTokenOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<InternalTokenOptions>(options, logger, encoder)
{
    /// <summary>The limiter asks before any policy runs, and a service with user sign-in never tries this scheme by default.</summary>
    public static bool HasValidToken(HttpContext context) => Matches(
        context.Request,
        context.RequestServices.GetRequiredService<IOptionsMonitor<InternalTokenOptions>>().Get(AuthSetup.InternalPolicy).Token);

    private static bool Matches(HttpRequest request, string token) =>
        token.Length > 0 && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(request.Headers[HttpWorkerClient.TokenHeader].ToString()),
            Encoding.UTF8.GetBytes(token));

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Matches(Request, Options.Token))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "internal")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
