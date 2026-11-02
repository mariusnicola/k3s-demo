using BatchTracker.Application;
using BatchTracker.Domain;

namespace BatchTracker.Live;

/// <summary>
/// What the hub answers from: state counts of every batch, the rows a scan named without their steps for reports, and
/// the newest full rows per farm for snapshots. Holds no lock: the hub's lock covers every call.
/// </summary>
public sealed class LiveRows
{
    private readonly Dictionary<string, Dictionary<string, Versioned>> newest = [];
    private readonly StateCounts counts = new();
    private Dictionary<string, Versioned> rows = [];
    private Dictionary<string, BatchState?>? writtenDuringScan;

    public int FullRowsHeld => newest.Values.Sum(farm => farm.Count);

    /// <summary>Writers can arrive out of order, so a row older than the one held changes nothing. False when it did.</summary>
    public bool Apply(Versioned row)
    {
        var known = rows.TryGetValue(row.Batch.Id, out var held);
        if (known && held!.Batch.Version > row.Batch.Version) return false;

        var from = known ? held!.Batch.State : MovedFrom(row.Batch);
        counts.Move(row.Batch, from);
        rows[row.Batch.Id] = WithoutSteps(row);
        writtenDuringScan?.TryAdd(row.Batch.Id, from);
        KeepNewest(row);
        return true;
    }

    public List<Batch> Visible(Caller caller) =>
        rows.Values.Where(r => caller.HasFarm(r.Batch.FarmId)).Select(r => r.Batch).ToList();

    /// <summary>The rows held answer pending, blocked and flagged. Only the state counts need every batch.</summary>
    public Summary SummaryFor(Caller caller, IReadOnlyCollection<Batch> visible) =>
        Reports.SummaryOf(visible) with { ByState = counts.ByState(caller) };

    public List<Versioned> NewestFor(Caller caller) => newest.Where(f => caller.HasFarm(f.Key))
        .SelectMany(f => f.Value.Values)
        .OrderByDescending(r => r.Batch.CreatedAt)
        .Take(HopHub.RowsPerSnapshot)
        .ToList();

    public int Total(Caller caller) => counts.Total(caller);

    /// <summary>From here each write records where it took its batch from, for the scan to settle against.</summary>
    public void BeginReconcile() => writtenDuringScan = [];

    /// <summary>Swaps in a finished scan, keeping anything written while it ran. Returns the rows to read in full.</summary>
    public IReadOnlyList<Batch> FinishReconcile(LiveScan scan)
    {
        var next = scan.Rows.ToDictionary(r => r.Batch.Id);
        counts.Replace(scan.Counts);
        foreach (var (id, from) in writtenDuringScan ?? [])
        {
            if (!rows.TryGetValue(id, out var held)) continue;
            var seen = next.TryGetValue(id, out var scanned);
            if (seen && held.Batch.Version <= scanned!.Batch.Version) continue;
            next[id] = held;
            counts.Move(held.Batch, seen ? scanned!.Batch.State : from);
        }
        writtenDuringScan = null;
        rows = next;

        foreach (var farm in newest.Values)
        {
            foreach (var gone in farm.Keys.Where(id => !rows.ContainsKey(id)).ToList()) farm.Remove(gone);
        }
        return rows.Values.GroupBy(r => r.Batch.FarmId)
            .SelectMany(farm => farm.OrderByDescending(r => r.Batch.CreatedAt).Take(HopHub.RowsPerSnapshot))
            .Where(r => !IsHeldInFull(r))
            .Select(r => r.Batch)
            .ToList();
    }

    private bool IsHeldInFull(Versioned row) =>
        newest.TryGetValue(row.Batch.FarmId, out var farm) && farm.TryGetValue(row.Batch.Id, out var held) && held.ETag == row.ETag;

    /// <summary>
    /// Where a write took a batch this hub does not hold. Only a write that created a batch or changed its state raises
    /// a new pending event: it adds a new batch and moves any other from its last step. Any other write returns the
    /// batch's own state, which moves nothing. A requeue looks like a new event, so past the cap it can move a count
    /// until the next scan.
    /// </summary>
    private static BatchState? MovedFrom(Batch batch)
    {
        if (batch.Pending is not { Status: PendingStatus.Pending, ReplayOf: null }) return batch.State;
        return batch.Steps.Count <= 1 ? null : batch.Steps[^2].State;
    }

    private void KeepNewest(Versioned row)
    {
        if (!newest.TryGetValue(row.Batch.FarmId, out var farm)) newest[row.Batch.FarmId] = farm = [];
        farm[row.Batch.Id] = row;
        if (farm.Count > HopHub.RowsPerSnapshot) farm.Remove(farm.Values.MinBy(r => r.Batch.CreatedAt)!.Batch.Id);
    }

    private static Versioned WithoutSteps(Versioned row) => row with { Batch = row.Batch with { Steps = [] } };
}
