using System.Security.Claims;
using BatchTracker.Application;
using BatchTracker.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BatchTracker.Infrastructure;

public static class AuthSetup
{
    public const string InternalPolicy = "internal";

    /// <summary>Every route without its own policy is refused, so a forgotten policy fails closed.</summary>
    public static IServiceCollection AddUserAuth(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => SessionSettings.From(configuration));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => ReadBearer(options, configuration));

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            foreach (var permission in Permissions.All)
                options.AddPolicy(permission, p => p.RequireClaim(TokenIssuer.PermissionClaim, permission));
        });
        return services;
    }

    private static void ReadBearer(JwtBearerOptions options, IConfiguration configuration)
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = TokenIssuer.Issuer,
            ValidAudience = TokenIssuer.Issuer,
            IssuerSigningKey = TokenKeys.Public(configuration.Require("JWT_PUBLIC_KEY")),
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // A header always decides, so a bad bearer never falls back to the cookie.
                if (context.Request.Headers.ContainsKey("Authorization")) return Task.CompletedTask;
                context.Token = context.Request.Cookies[SessionCookie.Name];
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                if (context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub) is not { Length: > 0 })
                    context.Fail("Token has no subject.");
                return Task.CompletedTask;
            }
        };
    }

    public static IServiceCollection AddInternalAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication(InternalPolicy)
            .AddScheme<InternalTokenOptions, InternalTokenHandler>(InternalPolicy, o => o.Token = configuration.Require("INTERNAL_TOKEN"));
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.AddPolicy(InternalPolicy, p => p.RequireAuthenticatedUser());
        });
        return services;
    }
}
