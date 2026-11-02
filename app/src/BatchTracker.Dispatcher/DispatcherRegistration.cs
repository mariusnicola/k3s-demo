using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Infrastructure;

namespace BatchTracker.Dispatcher;

/// <summary>What the Dispatcher registers to deliver events.</summary>
public static class DispatcherRegistration
{
    /// <summary>The outbox, the dead letters, leadership and the runner that drives them.</summary>
    public static void AddDispatching(this IServiceCollection services)
    {
        services.AddSingleton(new DispatcherOptions());
        services.AddSingleton<DispatcherStatus>();
        services.AddSingleton<IPendingNow>(s => s.GetRequiredService<DispatcherStatus>());
        services.AddSingleton<PendingCount>();
        services.AddSingleton<Outbox>();
        services.AddBatchCounters(MetricNames.Dispatcher, MetricNames.DispatcherStages);
        services.AddSingleton<DeadLetters>();
        services.AddSingleton<LeadershipSignal>();
        services.AddSingleton<ILeadership>(s => s.GetRequiredService<LeadershipSignal>());
        services.AddSingleton(s =>
        {
            var logger = s.GetRequiredService<ILogger<DispatcherRunner>>();
            return new DispatcherRunner(
                s.GetRequiredService<ILeadership>(),
                s.GetRequiredService<IEventQueueFactory>(),
                s.GetRequiredService<IWorkerClient>(),
                s.GetRequiredService<Outbox>(),
                s.GetRequiredService<DeadLetters>(),
                s.GetRequiredService<DispatcherStatus>(),
                s.GetRequiredService<IHopReporter>(),
                s.GetRequiredService<TimeProvider>(),
                s.GetRequiredService<DispatcherOptions>(),
                (error, step) => logger.LogError(error, "Dispatcher step {Step} failed", step));
        });
    }
}
