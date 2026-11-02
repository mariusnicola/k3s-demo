using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BatchTracker.Infrastructure;

/// <summary>Drain plus Finish stays under the 30 s Kubernetes gives by default.</summary>
public sealed record ShutdownRules
{
    public TimeSpan Drain { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan Finish { get; init; } = TimeSpan.FromSeconds(20);
}

public sealed class ShutdownState
{
    private volatile bool draining;

    public bool Draining => draining;

    public void Begin() => draining = true;
}

/// <summary>
/// Runs before any hosted service stops, so readiness fails while the listener still serves.
/// The ingress takes seconds to notice, and a listener closed first would turn those seconds into errors.
/// </summary>
public sealed class DrainService(ShutdownState state, ShutdownRules rules, ILogger<DrainService> log) : IHostedLifecycleService
{
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        if (state.Draining) return;
        state.Begin();
        log.LogInformation("Stopping: readiness off, draining for {Seconds} s", rules.Drain.TotalSeconds);
        await Task.Delay(rules.Drain, cancellationToken);
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class ShutdownSetup
{
    /// <summary>For a service with no inbound traffic to drain.</summary>
    public static IServiceCollection AddShutdownLimit(this IServiceCollection services)
    {
        services.TryAddSingleton(new ShutdownRules());
        services.AddOptions<HostOptions>().Configure<ShutdownRules>((host, rules) => host.ShutdownTimeout = rules.Drain + rules.Finish);
        return services;
    }

    public static IServiceCollection AddGracefulShutdown(this IServiceCollection services)
    {
        services.AddShutdownLimit();
        services.TryAddSingleton<ShutdownState>();
        return services.AddHostedService<DrainService>();
    }

    public static void MapReady(this IEndpointRouteBuilder app) =>
        app.MapGet("/ready", (ShutdownState state) => state.Draining ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.Ok())
            .AllowAnonymous()
            .LimitedAsHealth();
}
