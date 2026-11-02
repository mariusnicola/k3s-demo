using System.Collections.Concurrent;
using System.Diagnostics;
using BatchTracker.Domain;

namespace BatchTracker.Line;

public sealed record SettledBatch(long Version, string ETag, BatchState State);

/// <summary>Tells the walks waiting on an event when the socket shows it done.</summary>
public sealed class LineSettled
{
    private readonly ConcurrentDictionary<string, SettledWatch> watches = new();
    private volatile bool connected;

    /// <summary>Up, a wait trusts the frames and reads only as a fallback. Down, it reads the way it did before.</summary>
    public bool Connected { get => connected; set => connected = value; }

    public int Watching => watches.Count;

    public SettledWatch Watch(string batchId) => watches[batchId] = new SettledWatch(() => watches.TryRemove(batchId, out _));

    public void Saw(string etag, Batch batch)
    {
        if (batch.Pending is null && watches.TryGetValue(batch.Id, out var watch))
            watch.Saw(new SettledBatch(batch.Version, etag, batch.State));
    }
}

/// <summary>Opened before the read it follows, so a frame that lands between them still counts.</summary>
public sealed class SettledWatch(Action end) : IDisposable
{
    private readonly SemaphoreSlim changed = new(0);
    private volatile SettledBatch? newest;

    public void Saw(SettledBatch seen)
    {
        newest = seen;
        changed.Release();
    }

    /// <summary>Null when the wait ran out. A frame no newer than the read is the step before, whose ETag is already spent.</summary>
    public async Task<SettledBatch?> AfterAsync(long version, TimeSpan wait, CancellationToken cancel)
    {
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            if (newest is { } seen && seen.Version > version) return seen;
            var left = wait - Stopwatch.GetElapsedTime(start);
            if (left <= TimeSpan.Zero || !await changed.WaitAsync(left, cancel)) return null;
        }
    }

    public void Dispose() => end();
}
