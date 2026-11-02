namespace BatchTracker.Application.Dispatching;

/// <summary>
/// A store with nothing to send is read less and less, and the first row moved restores the pace. The sweep is the only
/// thing that finds a new event, so it cannot stop. Unmeasured: the cap of 20 times 200 ms, chosen as the longest a first
/// event should wait after an idle spell.
/// </summary>
public sealed class SweepPace(DispatcherOptions options)
{
    private int backoff = 1;

    /// <summary>The wait before the next sweep, given what the last one moved.</summary>
    public TimeSpan After(int moved)
    {
        backoff = moved > 0 ? 1 : Math.Min(backoff * 2, options.MaxIdleBackoff);
        return options.SweepInterval * backoff;
    }
}
