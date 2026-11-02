using BatchTracker.Application;
using BatchTracker.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BatchTracker.Api;

/// <summary>What the API registers to run batches.</summary>
public static class ApiRegistration
{
    /// <summary>The store, the clients around it, the counters and the service that uses them.</summary>
    public static void AddBatchWork(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddBatchStore(configuration);
        services.AddHopReporter(configuration);
        services.AddWorkerClient(configuration);
        services.AddBusProbe(configuration);
        services.AddBatchCounters(MetricNames.Api, MetricNames.ApiStages);
        services.AddSingleton<BatchService>();
        services.AddSingleton<BatchBundle>();
        services.AddSingleton(new StoreWatchRules());
        services.AddSingleton(s => new StoreWatch(s.GetRequiredService<IBatchStore>(), TimeProvider.System, s.GetRequiredService<StoreWatchRules>(), s.GetRequiredService<ILogger<StoreWatch>>()));
        services.AddHostedService(s => s.GetRequiredService<StoreWatch>());
    }
}
