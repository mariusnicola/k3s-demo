/// <summary>
/// Keeps the socket on the newest cluster read. A cycle's checks can outlast a watch's read,
/// and a report built from the read that began the cycle would take a new pod off the map again.
/// </summary>
public sealed class ReportCycle(Func<Task<Snapshot>> collect, Func<Report?> latest, Action<Report> publish)
{
    private readonly object sync = new();
    private long started;
    private long newestRead;
    private Snapshot? newest;

    /// <summary>A failed read leaves the map alone: the cycle reports it, and no pods would read as all pods gone.</summary>
    public async Task Refresh()
    {
        if (latest() is null) return;
        await Read();
        var seen = Newest();
        if (seen.Error is not null || latest() is not { } last) return;
        publish(last with { Nodes = seen.Nodes, Pods = seen.Pods, Caps = seen.Caps, CapIssues = seen.CapIssues, DispatcherLeader = seen.DispatcherLeader, Store = seen.Store, Workers = seen.Workers });
    }

    /// <summary>An admin's socket and /health want a report before the first checks end, which can take 20 s.</summary>
    public async Task<Check[]> Run(Func<Snapshot, Task<Check[]>> runChecks)
    {
        var snapshot = await Read();
        if (latest() is null) publish(Built(false, [], snapshot));
        var checks = await runChecks(snapshot);
        publish(Built(checks.All(c => c.Healthy), checks, Newest()));
        return checks;
    }

    private static Report Built(bool healthy, Check[] checks, Snapshot read) =>
        new(DateTimeOffset.UtcNow, healthy, checks, read.Nodes, read.Pods, read.Caps, read.CapIssues, read.DispatcherLeader, read.Store, read.Workers);

    private async Task<Snapshot> Read()
    {
        var order = Interlocked.Increment(ref started);
        var snapshot = await collect();
        lock (sync)
        {
            if (order > newestRead)
            {
                newestRead = order;
                newest = snapshot;
            }
        }
        return snapshot;
    }

    private Snapshot Newest()
    {
        lock (sync) return newest!;
    }
}
