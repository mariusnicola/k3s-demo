using BatchTracker.Application;
using BatchTracker.Infrastructure;

namespace BatchTracker.Live;

/// <summary>Scan timing, a type of its own so a test can shorten it.</summary>
public sealed record LiveScanRules
{
    /// <summary>Exists to end a hung scan, so it sits well above a scan that is merely slow.</summary>
    public TimeSpan AttemptLimit { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Scans the store behind a running pod and heals values lost with a fire-and-forget POST, one scan a minute.
/// The first scan never holds the start: hops need no store, and a pod that waited for one would lose them.
/// </summary>
public sealed class LiveReconciler(HopHub hub, IBatchStore store, ILogger<LiveReconciler> logger, LiveScanRules? rules = null) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly LiveScanRules limits = rules ?? new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; !await ReconcileAsync(stoppingToken); attempt++)
            await Task.Delay(RetryRules.Backoff(attempt, Random.Shared.NextDouble()), stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await ReconcileAsync(stoppingToken);
    }

    /// <summary>False when the scan did not finish. Only a stop leaves here as an exception, because any other would stop the host.</summary>
    private async Task<bool> ReconcileAsync(CancellationToken stopping)
    {
        using var action = Correlation.Use(Correlation.New());
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        limit.CancelAfter(limits.AttemptLimit);
        try
        {
            hub.BeginReconcile();
            var scan = await store.FindForLiveAsync(limit.Token);
            if (scan.Capped) logger.LogWarning("Live holds only {Max} open batches, so pending, blocked and flagged may read short", LiveScan.MaxOpen);
            var missing = hub.FinishReconcile(scan);
            foreach (var batch in missing)
            {
                if (await store.GetAsync(batch.FarmId, batch.Id, limit.Token) is { } row) hub.Apply(row);
            }
            return true;
        }
        catch (Exception e) when (!stopping.IsCancellationRequested)
        {
            logger.LogWarning("Live could not reconcile with the store: {Type}", e.GetType().Name);
            return false;
        }
    }
}
