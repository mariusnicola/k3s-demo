using System.Diagnostics;
using BatchTracker.Application;
using BatchTracker.Domain;

namespace BatchTracker.Infrastructure;

/// <summary>Reports every write in one place, so API, Worker and Dispatcher cannot forget.</summary>
public sealed class ReportingBatchStore(IBatchStore store, IChangeReporter changes, StoreClock clock, IBatchCounters? counters) : IBatchStore
{
    public async Task<Versioned?> CreateAsync(Batch batch, CancellationToken token)
    {
        var stamped = await StampAsync(batch, token);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var saved = await store.CreateAsync(stamped, token);
            if (saved is not null) changes.Report(saved);
            return saved;
        }
        finally
        {
            counters?.Time(Stages.Store, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    public async Task<Versioned?> ReplaceAsync(Batch batch, string etag, CancellationToken token)
    {
        var stamped = await StampAsync(batch, token);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var saved = await store.ReplaceAsync(stamped, etag, token);
            if (saved is not null) changes.Report(saved);
            return saved;
        }
        finally
        {
            counters?.Time(Stages.Store, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    public async Task<IReadOnlyList<Versioned?>> WriteManyAsync(IReadOnlyList<BundleWrite> writes, CancellationToken token)
    {
        var stamped = new List<BundleWrite>(writes.Count);
        foreach (var write in writes) stamped.Add(write with { Batch = await StampAsync(write.Batch, token) });
        var started = Stopwatch.GetTimestamp();
        try
        {
            var saved = await store.WriteManyAsync(stamped, token);
            foreach (var row in saved.OfType<Versioned>()) changes.Report(row);
            return saved;
        }
        finally
        {
            counters?.Time(Stages.Store, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    /// <summary>No change report: these rows have no steps to show, and the Worker's write reports the whole row next.</summary>
    public async Task<IReadOnlyList<Versioned>> MarkSentAsync(IReadOnlyList<Versioned> rows, DateTimeOffset at, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await store.MarkSentAsync(rows, at, token);
        }
        finally
        {
            counters?.Time(Stages.Store, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    /// <summary>Every path to a new pending event ends here, so none can forget. A clock never read leaves it unstamped: the write matters more.</summary>
    private async ValueTask<Batch> StampAsync(Batch batch, CancellationToken token) =>
        batch.Pending is { Status: PendingStatus.Pending, StoredAt: null } pending && await clock.NowAsync(token) is { } now
            ? batch with { Pending = pending with { StoredAt = now } }
            : batch;

    public Task<Versioned?> GetAsync(string farmId, string id, CancellationToken token) => store.GetAsync(farmId, id, token);
    public Task<IReadOnlyList<Versioned>> GetManyAsync(IReadOnlyList<BatchKey> keys, CancellationToken token) => store.GetManyAsync(keys, token);
    public Task<IReadOnlyList<Versioned>> ListAsync(BatchFilter filter, CancellationToken token) => store.ListAsync(filter, token);
    public Task<IReadOnlyList<Versioned>> FindByPendingStatusAsync(string status, int max, CancellationToken token) => store.FindByPendingStatusAsync(status, max, token);
    public Task<IReadOnlyList<Versioned>> FindSentBeforeAsync(DateTimeOffset cutoff, int max, CancellationToken token) => store.FindSentBeforeAsync(cutoff, max, token);
    public Task<IReadOnlyList<Versioned>> FindInStateAsync(BatchState state, CancellationToken token) => store.FindInStateAsync(state, token);
    public Task<IReadOnlyList<Batch>> FindForReportsAsync(IReadOnlyList<string>? farms, CancellationToken token) => store.FindForReportsAsync(farms, token);
    public Task<LiveScan> FindForLiveAsync(CancellationToken token) => store.FindForLiveAsync(token);
    public Task<bool> ReachableAsync(CancellationToken token) => store.ReachableAsync(token);
}
