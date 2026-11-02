using System.Collections.Concurrent;

/// <summary>
/// When a Deployment counts as failing. Below its minimum it fails at once. Above the minimum and below spec
/// is a scale-up in progress, which fails only when it outlasts the limit.
/// </summary>
public sealed class ReadyRule(TimeProvider clock)
{
    /// <summary>Above the slowest Worker start read so far, 187 s after a node restart.</summary>
    public static readonly TimeSpan ScaleUpLimit = TimeSpan.FromSeconds(240);

    private readonly ConcurrentDictionary<string, DateTimeOffset> below = [];

    public bool Failing(string deployment, int spec, int ready, int minimum)
    {
        if (ready >= spec && spec > 0)
        {
            below.TryRemove(deployment, out _);
            return false;
        }
        if (spec == 0 || ready < minimum) return true;
        var now = clock.GetUtcNow();
        return now - below.GetOrAdd(deployment, now) >= ScaleUpLimit;
    }
}
