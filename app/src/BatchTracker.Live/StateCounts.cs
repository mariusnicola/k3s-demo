using BatchTracker.Application;
using BatchTracker.Domain;

namespace BatchTracker.Live;

/// <summary>Batches per farm and state, from the last scan moved along by each write. Off by a few until the next scan.</summary>
public sealed class StateCounts
{
    private readonly Dictionary<(string Farm, string State), int> counts = [];

    /// <summary>Takes a scan's counts as the new truth.</summary>
    public void Replace(IEnumerable<StateCount> scan)
    {
        counts.Clear();
        foreach (var count in scan) counts[(count.FarmId, count.State)] = count.Count;
    }

    /// <summary>A null source means a batch this count has not seen.</summary>
    public void Move(Batch batch, BatchState? from)
    {
        if (from == batch.State) return;
        if (from is { } source) Take(batch.FarmId, source.ToString());
        var key = (batch.FarmId, batch.State.ToString());
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }

    /// <summary>Counts of the farms the caller may see, with every state present.</summary>
    public IReadOnlyDictionary<string, int> ByState(Caller caller)
    {
        var byState = Enum.GetNames<BatchState>().ToDictionary(n => n, _ => 0);
        foreach (var (key, count) in counts)
        {
            if (caller.HasFarm(key.Farm)) byState[key.State] += count;
        }
        return byState;
    }

    /// <summary>All batches the caller may see.</summary>
    public int Total(Caller caller) => counts.Where(c => caller.HasFarm(c.Key.Farm)).Sum(c => c.Value);

    /// <summary>A write can arrive before the first scan, with nothing yet to take from.</summary>
    private void Take(string farm, string state)
    {
        if (counts.GetValueOrDefault((farm, state)) > 0) counts[(farm, state)]--;
    }
}
