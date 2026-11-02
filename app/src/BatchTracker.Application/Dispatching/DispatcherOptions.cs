namespace BatchTracker.Application.Dispatching;

public sealed record DispatcherOptions
{
    public const string RefusedText = "Worker refused the event.";

    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public int MaxIdleBackoff { get; init; } = 20;
    public TimeSpan StaleSweepEvery { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ResendAfter { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan WorkerProbeInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReceiveWait { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ErrorDelay { get; init; } = TimeSpan.FromSeconds(1);
    public int SweepSize { get; init; } = 200;
    public int DeliveryConcurrency { get; init; } = 40;

    /// <summary>A letter can be a pack, and a pack is up to 400 row writes under one 30 s lock.</summary>
    public int DeadLetterRead { get; init; } = 1;
    public int WorkerFailuresToStop { get; init; } = 3;

    /// <summary>The app pods' scrape interval, so each scrape carries a new reading.</summary>
    public TimeSpan PendingCountEvery { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed class DispatcherStatus : IPendingNow
{
    private const long NoTerm = 0;
    private const long NoCount = -1;

    private long pendingNow = NoCount;
    private volatile bool leader;
    private volatile bool connected;
    private long sweptAt = NoTerm;

    public bool Leader => leader;
    public bool Connected { get => connected; set => connected = value; }

    public long? PendingNow
    {
        get => Interlocked.Read(ref pendingNow) is var count && count == NoCount ? null : count;
        set => Interlocked.Exchange(ref pendingNow, value ?? NoCount);
    }

    /// <summary>
    /// Null with no term. Measured here because the nodes share no clock. A sweep with nothing to send still counts,
    /// so a rising figure means the sweep stopped, whatever is pending.
    /// </summary>
    public double? SecondsSinceSweep =>
        Volatile.Read(ref sweptAt) is var at && at == NoTerm ? null : System.Diagnostics.Stopwatch.GetElapsedTime(at).TotalSeconds;

    public void Swept() => Volatile.Write(ref sweptAt, System.Diagnostics.Stopwatch.GetTimestamp());

    /// <summary>Starts the figure too, so a term that cannot even connect still climbs.</summary>
    public void TermStarted()
    {
        Swept();
        leader = true;
    }

    public void TermEnded()
    {
        connected = false;
        leader = false;
        Volatile.Write(ref sweptAt, NoTerm);
    }
}
