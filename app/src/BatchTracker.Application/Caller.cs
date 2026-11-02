using BatchTracker.Domain;

namespace BatchTracker.Application;

public sealed record Caller(string User, IReadOnlyList<string> Permissions, IReadOnlyList<string> Farms)
{
    public const string AllFarms = "*";

    public bool HasAllFarms => Farms.Contains(AllFarms);
    public bool HasFarm(string farmId) => HasAllFarms || Farms.Contains(farmId);

    public IReadOnlyList<string>? FarmScope => HasAllFarms ? null : Farms;
}

public static class DemoUsers
{
    public static readonly IReadOnlyList<string> KnownFarms = ["farm-a", "farm-b", "farm-c"];

    private static readonly IReadOnlyList<Caller> Users =
    [
        new("olivia", [Permissions.BatchRead, Permissions.BatchWrite], ["farm-a", "farm-b"]),
        new("ian", [Permissions.BatchRead, Permissions.BatchInspect], [Caller.AllFarms]),
        new("ada", Permissions.All, [Caller.AllFarms]),
        new("viewer", [Permissions.BatchRead], ["farm-a"])
    ];

    /// <summary>Not a person, so no sign-in reaches it: it walks batches and cannot administer.</summary>
    public static readonly Caller LineMachine = new(
        "line", [Permissions.BatchRead, Permissions.BatchWrite, Permissions.BatchInspect], [Caller.AllFarms]);

    /// <summary>Proves the routes answer. It holds no permission and no farm, so it can read and change nothing.</summary>
    public static readonly Caller MonitorMachine = new("monitor", [], []);

    public static Caller? Find(string? user) => Users.FirstOrDefault(u => u.User == user);
}
