using BatchTracker.Application.Dispatching;
using BatchTracker.Infrastructure;
using k8s;
using k8s.LeaderElection;
using k8s.LeaderElection.ResourceLock;

namespace BatchTracker.Dispatcher;

/// <summary>Lets test hosts find this assembly's entry point.</summary>
public sealed class DispatcherMarker;

public static class LeaseRelease
{
    /// <summary>Without this the next pod waits out the lease, and events sit undelivered for that long.</summary>
    public static async Task ReleaseAsync(ILock lease, CancellationToken token)
    {
        var record = await lease.GetAsync(token);
        if (record.HolderIdentity != lease.Identity) return;
        await lease.UpdateAsync(new LeaderElectionRecord
        {
            HolderIdentity = "",
            LeaseDurationSeconds = 1,
            AcquireTime = record.AcquireTime,
            RenewTime = DateTime.UtcNow,
            LeaderTransitions = record.LeaderTransitions
        }, token);
    }
}

public sealed class RunnerService(DispatcherRunner runner) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => runner.RunAsync(stoppingToken);
}

public sealed class KubernetesElection(
    LeadershipSignal signal, IKubernetes client, IConfiguration configuration, ILogger<KubernetesElection> logger)
    : BackgroundService
{
    private const string LeaseName = "dispatcher";
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    private LeaseLock? lease;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (lease is null) return;
        try
        {
            await LeaseRelease.ReleaseAsync(lease, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Lease not released, it runs out on its own");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var identity = configuration.PodName();
        var leaseNamespace = configuration["LEASE_NAMESPACE"] ?? "batch-tracker";

        while (!stoppingToken.IsCancellationRequested)
        {
            // Giving up after 5 s of failed renewals stops this pod before the 10 s lease can pass to another.
            lease = new LeaseLock(client, leaseNamespace, LeaseName, identity);
            using var elector = new LeaderElector(new LeaderElectionConfig(lease)
            {
                LeaseDuration = TimeSpan.FromSeconds(10),
                RenewDeadline = TimeSpan.FromSeconds(5),
                RetryPeriod = TimeSpan.FromSeconds(2)
            });
            elector.OnStartedLeading += signal.Gain;
            elector.OnStoppedLeading += signal.Lose;
            elector.OnError += e => logger.LogWarning(e, "Lease call failed");
            try
            {
                await elector.RunUntilLeadershipLostAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Leader election failed");
                signal.Lose();
                await Task.Delay(RestartDelay, stoppingToken);
            }
        }
    }
}

/// <summary>Runs on every pod, so a pod that loses its term clears its figure on the next read.</summary>
public sealed class PendingCountService(PendingCount count, DispatcherOptions options, ILogger<PendingCountService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PendingCountEvery);
        do
        {
            try
            {
                await count.ReadAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Pending count failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
