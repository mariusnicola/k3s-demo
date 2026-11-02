namespace BatchTracker.Domain;

public static class Permissions
{
    public const string BatchRead = "batch.read";
    public const string BatchWrite = "batch.write";
    public const string BatchInspect = "batch.inspect";
    public const string OpsAdmin = "ops.admin";

    public static readonly IReadOnlyList<string> All = [BatchRead, BatchWrite, BatchInspect, OpsAdmin];
}
