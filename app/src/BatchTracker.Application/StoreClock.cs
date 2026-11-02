namespace BatchTracker.Application;

/// <summary>What only the store can answer: its own clock, and how many events are under way.</summary>
public interface IStoreReadings
{
    Task<DateTimeOffset> ClockAsync(CancellationToken token);

    /// <summary>Rows waiting to be sent or sent and not yet recorded. Dead rows wait on a person, so they are out.</summary>
    Task<long> CountInProgressAsync(CancellationToken token);
}

/// <summary>
/// The store's clock as this pod sees it. Pods on two nodes share no clock, so a time written on one and read on another
/// is only right when both come from here. The store is read at most once per 30 s; between reads the pod's monotonic
/// timer carries it on, which keeps the hot path free of a round trip. The error is at most half each pod's last read.
/// </summary>
public sealed class StoreClock(IStoreReadings store, TimeProvider time)
{
    private static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim reading = new(1, 1);
    private volatile Read? last;
    private long triedAt;
    private bool tried;

    private sealed record Read(DateTimeOffset Store, long At);

    /// <summary>Null only when the store has never answered. Timing never fails the work it times.</summary>
    public async ValueTask<DateTimeOffset?> NowAsync(CancellationToken token)
    {
        if (Due()) await ReadAsync(token);
        return last is { } read ? read.Store + time.GetElapsedTime(read.At) : null;
    }

    private bool Due() => !Volatile.Read(ref tried) || time.GetElapsedTime(Interlocked.Read(ref triedAt)) >= ReadEvery;

    private async Task ReadAsync(CancellationToken token)
    {
        await reading.WaitAsync(token);
        try
        {
            if (!Due()) return;
            var before = time.GetTimestamp();
            Interlocked.Exchange(ref triedAt, before);
            Volatile.Write(ref tried, true);
            var store = await ReadStoreAsync(token);
            var after = time.GetTimestamp();
            if (store is { } answer) last = new Read(answer + time.GetElapsedTime(before, after) / 2, after);
        }
        finally
        {
            reading.Release();
        }
    }

    private async Task<DateTimeOffset?> ReadStoreAsync(CancellationToken token)
    {
        try
        {
            return await store.ClockAsync(token);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return null;
        }
    }
}
