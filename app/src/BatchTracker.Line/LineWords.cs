namespace BatchTracker.Line;

/// <summary>One fixed text per reason, so an entry can never carry exception text in these fields.</summary>
public static class LineWords
{
    private static readonly Dictionary<string, (string Impact, string Next)> ByReason = new()
    {
        [LineReason.Started] = ("A new batch was created.", "Nothing to do."),
        [LineReason.Closed] = ("The batch finished every step.", "Nothing to do."),
        [LineReason.Rejected] = ("The batch was rejected at inspection, on purpose.", "Nothing to do."),
        [LineReason.EventTimeout] = ("The batch stays at its last step and the Line left it there.", "Check the batch status. A blocked batch needs an admin to replay it."),
        [LineReason.HttpRefused] = ("The API refused the step, so the batch did not move.", "Read the HTTP status and fix what the API refused."),
        [LineReason.NetworkFailure] = ("The step may or may not have reached the API.", "Check the API is up, then read the batch to see where it stands."),
        [LineReason.LoginFailure] = ("The step did not run, so the batch did not move.", "Check the Line machine token setting."),
        [LineReason.Unknown] = ("The walk of this batch stopped at this step.", "Find the correlation ID in the service logs, and read the batch.")
    };

    /// <summary>What this reason did to the batch.</summary>
    public static string ImpactOf(string reason) => ByReason[reason].Impact;

    /// <summary>What to do about this reason.</summary>
    public static string NextActionOf(string reason) => ByReason[reason].Next;
}
