using k8s.Models;

/// <summary>What the report shows of a pod, kept apart from the Kubernetes calls so each rule has a test.</summary>
public static class PodRules
{
    public static bool Shown(V1Pod pod) => pod.Status?.Phase != "Succeeded";

    /// <summary>Set by the delete itself, so it starts when the feed says "pod deleted" and the pod may still serve.</summary>
    public static bool Terminating(V1Pod pod) => pod.Metadata.DeletionTimestamp is not null;

    public static bool HoldsResources(V1Pod pod) => pod.Metadata.DeletionTimestamp is null && pod.Status?.Phase is "Running" or "Pending";

    public static bool Ready(V1Pod pod) => pod.Status?.ContainerStatuses is { Count: > 0 } statuses && statuses.All(s => s.Ready);
}

/// <summary>Who leads. A stopping Dispatcher blanks the holder, and one that died leaves its name on a lease that ran out.</summary>
public static class LeaseRule
{
    public static string? Leader(V1Lease lease, DateTimeOffset now)
    {
        var spec = lease.Spec;
        if (string.IsNullOrEmpty(spec?.HolderIdentity) || spec.RenewTime is not { } renewed || spec.LeaseDurationSeconds is not { } seconds) return null;
        return new DateTimeOffset(renewed, TimeSpan.Zero).AddSeconds(seconds) < now ? null : spec.HolderIdentity;
    }
}

/// <summary>What a Dispatcher's /status says. The age comes from the pod, because the nodes share no clock.</summary>
public record DispatcherState(bool Leader, bool Connected, double? SecondsSinceSweep);

/// <summary>A renewing lease proves only that the election runs. An idle sweep still runs every 4 s at most, so this reads the sweep itself.</summary>
public static class SweepRule
{
    private const double MostSeconds = 60;

    public static string? Problem(string holder, DispatcherState state)
    {
        if (!state.Leader) return $"{holder} holds the lease but is not leading, so nothing sweeps";
        if (state.SecondsSinceSweep is not { } seconds) return $"{holder} reports no sweep";
        return seconds > MostSeconds ? $"{holder} last swept {(int)seconds} s ago" : null;
    }
}

/// <summary>Deployment checks cannot see a job, so a nightly run that failed would stay silent until someone asked.</summary>
public static class NightlyRule
{
    private static readonly TimeSpan MostBetweenRuns = TimeSpan.FromHours(25);

    public static string? Problem(V1CronJob cron, IEnumerable<V1Job> jobs, DateTimeOffset now)
    {
        if (cron.Spec.Suspend == true) return "suspended";
        var last = jobs.Where(j => j.Metadata.OwnerReferences?.Any(o => o.Name == cron.Metadata.Name) == true)
            .OrderByDescending(j => j.Status?.StartTime)
            .FirstOrDefault();
        var failed = last?.Status?.Conditions?.FirstOrDefault(c => c.Type == "Failed" && c.Status == "True");
        if (failed is not null) return $"last run {last!.Metadata.Name} failed: {failed.Reason}";
        return cron.Status?.LastScheduleTime is { } scheduled && now - new DateTimeOffset(scheduled, TimeSpan.Zero) > MostBetweenRuns
            ? $"no run since {scheduled:u}"
            : null;
    }
}
