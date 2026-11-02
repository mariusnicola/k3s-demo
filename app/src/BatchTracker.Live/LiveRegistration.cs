using BatchTracker.Application;
using BatchTracker.Infrastructure;

namespace BatchTracker.Live;

/// <summary>What Live registers to hold its state.</summary>
public static class LiveRegistration
{
    /// <summary>The store it scans, the hub and the service log.</summary>
    public static void AddLiveState(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddBatchStore(configuration);
        services.AddSingleton<HopHub>();
        services.AddSingleton<ServiceLog>();
        services.AddSingleton<ILogSink>(s => s.GetRequiredService<ServiceLog>());
    }
}
