namespace BatchTracker.Domain;

public enum BatchState { Created, Dose1, Dose2, Inspected, Shipped, Closed, Rejected }

public static class PendingStatus
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Dead = "dead";
}

public sealed record Flag(string Type, DateTimeOffset At);

public sealed record Step
{
    public required BatchState State { get; init; }
    public required DateTimeOffset At { get; init; }
    public required string By { get; init; }
    public required string TraceId { get; init; }
    public string RecordId { get; init; } = "";
    public string Hash { get; init; } = "";
    public string PreviousHash { get; init; } = "";
}

public sealed record PendingEvent
{
    public required string EventId { get; init; }
    public required string TraceId { get; init; }
    public string? CorrelationId { get; init; }

    /// <summary>The id of the event that died, set when a replay gives this one a new id.</summary>
    public string? ReplayOf { get; init; }

    public required string Status { get; init; }

    /// <summary>On the store's clock, not a pod's, so a Worker on another node can subtract it from its own read of that clock.</summary>
    public DateTimeOffset? StoredAt { get; init; }

    public DateTimeOffset? SentAt { get; init; }
    public string? Error { get; init; }
}

public sealed record Batch
{
    public required string Id { get; init; }
    public required string FarmId { get; init; }
    public required string Product { get; init; }
    public required BatchState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? Reason { get; init; }
    public long Version { get; init; }
    public bool FailNext { get; init; }
    public IReadOnlyList<Flag> Flags { get; init; } = [];
    public IReadOnlyList<Step> Steps { get; init; } = [];
    public PendingEvent? Pending { get; init; }

    public bool IsBlocked() => Pending?.Status == PendingStatus.Dead;
}
