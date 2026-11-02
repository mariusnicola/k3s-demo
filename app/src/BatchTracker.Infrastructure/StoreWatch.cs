using BatchTracker.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BatchTracker.Infrastructure;

/// <summary>
/// Timing of the background store read. All unmeasured. The read limit matches the Worker delivery limit of 10 s: a store
/// slower than that cannot serve a delivery. Fresh for 15 s leaves room for one read at the limit and the interval after it.
/// </summary>
public sealed record StoreWatchRules
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ReadLimit { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan FreshFor { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Under the kubelet's 3 s timeout, so a pod that just started answers instead of timing out.</summary>
    public TimeSpan FirstWait { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Reads the store apart from any probe and keeps the answer, so a slow store cannot race the kubelet's own timeout.
/// Good means this pod finished a store read within the limit, not longer ago than FreshFor.
/// </summary>
public sealed class StoreWatch(IBatchStore store, TimeProvider time, StoreWatchRules rules, ILogger<StoreWatch> log) : BackgroundService
{
    /// <summary>A store read that finished, and how long it took.</summary>
    private sealed record Read(DateTimeOffset At, long Ms);

    private readonly TaskCompletionSource firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile Read? last;

    public bool Good => last is { } read && time.GetUtcNow() - read.At <= rules.FreshFor;

    /// <summary>How long the last good read took, so a slow store shows while it still reads as good.</summary>
    public long? LastReadMs => last?.Ms;

    /// <summary>Waits briefly for the first read only: after that the answer is already held.</summary>
    public async Task<bool> GoodAsync(CancellationToken token)
    {
        try
        {
            await firstRead.Task.WaitAsync(rules.FirstWait, token);
        }
        catch (TimeoutException)
        {
        }
        return Good;
    }

    /// <summary>One read. Past the limit it is cut and counts for nothing.</summary>
    public async Task CheckAsync(CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(rules.ReadLimit);
        var start = time.GetUtcNow();
        try
        {
            if (await store.ReachableAsync(limit.Token))
            {
                var end = time.GetUtcNow();
                last = new Read(end, (long)(end - start).TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
        }
        catch (Exception e) when (!token.IsCancellationRequested)
        {
            // Type only: a driver's message can carry its connection.
            log.LogWarning("Store read failed: {Failure}", e.GetType().Name);
        }
        finally
        {
            firstRead.TrySetResult();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAsync(stoppingToken);
            await Task.Delay(rules.Interval, stoppingToken);
        }
    }
}
