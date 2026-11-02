namespace BatchTracker.Application.Dispatching;

/// <summary>
/// One pod counts, the leader, so two pods never add up to double. It runs beside the sweep and not inside it, because
/// a sweep paused by a Worker outage is exactly when this figure has to keep moving.
/// </summary>
public sealed class PendingCount(IStoreReadings store, DispatcherStatus status)
{
    public async Task ReadAsync(CancellationToken token)
    {
        if (!status.Leader)
        {
            status.PendingNow = null;
            return;
        }
        try
        {
            var count = await store.CountInProgressAsync(token);
            status.PendingNow = status.Leader ? count : null;
        }
        catch
        {
            status.PendingNow = null;
            throw;
        }
    }
}
