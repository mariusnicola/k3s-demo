using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.RateLimiting;
using BatchTracker.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace BatchTracker.Infrastructure;

public sealed record RateRule(int PerSecond, int Burst);

public sealed record RateRules
{
    public RateRule Anonymous { get; init; } = new(5, 10);
    public RateRule User { get; init; } = new(50, 100);
    public RateRule Machine { get; init; } = new(500, 1000);
    public RateRule DeepReady { get; init; } = new(1, 1);
    public RateRule Health { get; init; } = new(20, 40);
    public RateRule BrowserReport { get; init; } = new(2, 20);
}

/// <summary>Marks a health or metrics route, which has its own bucket per client, far above any probe.</summary>
public sealed class HealthRoute;

/// <summary>Marks a route one of our own services calls. A valid internal token is never refused, because a 429 there can lose real work.</summary>
public sealed class MachineRoute;

/// <summary>Marks the browser report route, whose bucket keeps a failing page off the anonymous budget that sign-in uses.</summary>
public sealed class BrowserReportRoute;

public static class ClientAddress
{
    /// <summary>
    /// A client can write the forwarded header, so it counts only from a proxy we trust, and then only its last entry,
    /// which the proxy wrote. A request with no peer came through no network, so nothing stands between it and the host.
    /// </summary>
    public static string Of(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (peer is { IsIPv4MappedToIPv6: true }) peer = peer.MapToIPv4();
        var believed = peer is null || context.RequestServices?.GetService<TrustedProxies>()?.Has(peer) == true;
        var forwarded = believed ? context.Request.Headers["X-Forwarded-For"].ToString() : "";
        var last = forwarded[(forwarded.LastIndexOf(',') + 1)..].Trim();
        return last.Length > 0 ? last : peer?.ToString() ?? "unknown";
    }
}

public static class RateLimits
{
    private const string DeepReadyPolicy = "deep-ready";
    private const string NoLimit = "no-limit";

    public static TokenBucketRateLimiter Bucket(RateRule rule, bool autoReplenish) => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = rule.Burst,
        TokensPerPeriod = rule.PerSecond,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        AutoReplenishment = autoReplenish,
        QueueLimit = 0
    });

    public static int WholeSeconds(TimeSpan wait) => Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));

    public static IServiceCollection AddRateLimits(this IServiceCollection services) => services.AddLimiter(PartitionOf);

    /// <summary>For a service with no users: only routes marked with a bucket are limited.</summary>
    public static IServiceCollection AddRouteRateLimits(this IServiceCollection services) => services.AddLimiter(MarkedPartitionOf);

    /// <summary>A route with no partition is not limited.</summary>
    private static IServiceCollection AddLimiter(
        this IServiceCollection services, Func<HttpContext, RateRules, (string Key, RateRule Rule)?> partitionOf)
    {
        services.AddSingleton(new RateRules());
        return services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                partitionOf(context, context.RequestServices.GetRequiredService<RateRules>()) is { } partition
                    ? RateLimitPartition.Get(partition.Key, _ => Bucket(partition.Rule, autoReplenish: false))
                    : RateLimitPartition.GetNoLimiter(NoLimit));
            options.AddPolicy(DeepReadyPolicy, context => RateLimitPartition.Get(DeepReadyPolicy, _ =>
                Bucket(context.RequestServices.GetRequiredService<RateRules>().DeepReady, autoReplenish: false)));
            options.OnRejected = RefuseAsync;
        });
    }

    /// <summary>Puts a route in the health bucket.</summary>
    public static TBuilder LimitedAsHealth<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new HealthRoute());

    /// <summary>Puts a route in the machine bucket.</summary>
    public static TBuilder LimitedAsMachine<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new MachineRoute());

    /// <summary>Puts a route in the browser report bucket.</summary>
    public static TBuilder LimitedAsBrowserReport<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new BrowserReportRoute());

    public static TBuilder LimitedAsDeepReady<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireRateLimiting(DeepReadyPolicy);

    /// <summary>The bucket a route's own mark names, whoever is calling.</summary>
    private static (string Key, RateRule Rule)? MarkedPartitionOf(HttpContext context, RateRules rules)
    {
        var metadata = context.GetEndpoint()?.Metadata;
        if (metadata?.GetMetadata<HealthRoute>() is not null) return ("health:" + ClientAddress.Of(context), rules.Health);
        if (metadata?.GetMetadata<BrowserReportRoute>() is not null) return ("browser-report:" + ClientAddress.Of(context), rules.BrowserReport);
        if (metadata?.GetMetadata<MachineRoute>() is null || InternalTokenHandler.HasValidToken(context)) return null;
        return ("machine-route:" + ClientAddress.Of(context), rules.Machine);
    }

    private static (string Key, RateRule Rule)? PartitionOf(HttpContext context, RateRules rules)
    {
        var metadata = context.GetEndpoint()?.Metadata;
        if (metadata?.GetMetadata<HealthRoute>() is not null || metadata?.GetMetadata<MachineRoute>() is not null
            || metadata?.GetMetadata<BrowserReportRoute>() is not null)
            return MarkedPartitionOf(context, rules);

        if (context.User.Identity?.IsAuthenticated != true)
            return ("address:" + ClientAddress.Of(context), rules.Anonymous);

        var subject = context.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return subject == DemoUsers.LineMachine.User
            ? ("machine:" + subject, rules.Machine)
            : ("user:" + subject, rules.User);
    }

    private static async ValueTask RefuseAsync(OnRejectedContext rejected, CancellationToken token)
    {
        var wait = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromSeconds(1);
        rejected.HttpContext.Response.Headers.RetryAfter = WholeSeconds(wait).ToString();
        await Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests",
            detail: "Too many requests. Wait before trying again.").ExecuteAsync(rejected.HttpContext);
    }
}

/// <summary>The proxies whose forwarded header is believed. A client writes that header too, so nobody else's is.</summary>
public sealed class TrustedProxies(IReadOnlyList<System.Net.IPNetwork> networks)
{
    /// <summary>Reads TRUSTED_PROXIES, a comma list of networks. A bad entry throws, so a typo stops the start.</summary>
    public static TrustedProxies From(string? setting) => new(
        (setting ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(System.Net.IPNetwork.Parse).ToList());

    /// <summary>True when this address is one of the proxies.</summary>
    public bool Has(System.Net.IPAddress address) => networks.Any(n => n.Contains(address));
}
