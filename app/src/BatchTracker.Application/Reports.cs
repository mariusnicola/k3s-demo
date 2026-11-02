using BatchTracker.Domain;

namespace BatchTracker.Application;

public sealed record Summary(
    IReadOnlyDictionary<string, int> ByState, int Pending, int Blocked, int Flagged);

public sealed record Alert(string Type, string BatchId, string FarmId, string Product, DateTimeOffset? At, string? Detail);

public static class Reports
{
    public static Summary SummaryOf(IReadOnlyCollection<Batch> batches)
    {
        var byState = Enum.GetNames<BatchState>().ToDictionary(n => n, _ => 0);
        foreach (var batch in batches) byState[batch.State.ToString()]++;
        return new Summary(
            byState,
            batches.Count(b => b.Pending is not null && !b.IsBlocked()),
            batches.Count(b => b.IsBlocked()),
            batches.Count(HasOpenFlag));
    }

    /// <summary>Counts of the farms the caller may see, with every state present.</summary>
    public static IReadOnlyDictionary<string, int> ByState(IReadOnlyList<StateCount> counts, Caller caller)
    {
        var byState = Enum.GetNames<BatchState>().ToDictionary(n => n, _ => 0);
        foreach (var count in counts.Where(c => caller.HasFarm(c.FarmId))) byState[count.State] += count.Count;
        return byState;
    }

    public static IReadOnlyList<Alert> AlertsOf(IReadOnlyCollection<Batch> batches)
    {
        var blocked = batches.Where(b => b.IsBlocked()).Select(b =>
            new Alert("Blocked", b.Id, b.FarmId, b.Product, b.Pending!.SentAt, b.Pending.Error));
        var flagged = batches.Where(HasOpenFlag).SelectMany(b => b.Flags.Select(f =>
            new Alert(f.Type, b.Id, b.FarmId, b.Product, f.At, null)));
        return [.. blocked, .. flagged];
    }

    /// <summary>Nothing clears a flag, so a finished batch would report one for ever.</summary>
    private static bool HasOpenFlag(Batch batch) =>
        batch.Flags.Count > 0 && batch.State is not (BatchState.Closed or BatchState.Rejected);
}
