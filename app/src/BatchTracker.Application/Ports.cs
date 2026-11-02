using BatchTracker.Domain;

namespace BatchTracker.Application;

public sealed record Versioned(Batch Batch, [property: System.Text.Json.Serialization.JsonPropertyName("etag")] string ETag);

public sealed record BatchFilter(IReadOnlyList<string>? Farms, BatchState? State, int Limit);

public interface IBatchStore
{
    /// <summary>Null when the id already exists, which is how a repeated create is told from a new one.</summary>
    Task<Versioned?> CreateAsync(Batch batch, CancellationToken token);
    Task<Versioned?> GetAsync(string farmId, string id, CancellationToken token);

    /// <summary>Null means the If-Match was lost.</summary>
    Task<Versioned?> ReplaceAsync(Batch batch, string etag, CancellationToken token);

    /// <summary>The rows that exist. Each write stands alone: the list answers in its order, null where it was refused.</summary>
    Task<IReadOnlyList<Versioned>> GetManyAsync(IReadOnlyList<BatchKey> keys, CancellationToken token);
    Task<IReadOnlyList<Versioned?>> WriteManyAsync(IReadOnlyList<BundleWrite> writes, CancellationToken token);

    Task<IReadOnlyList<Versioned>> ListAsync(BatchFilter filter, CancellationToken token);
    /// <summary>A sweep's read, without steps: a row from here is never written back whole.</summary>
    Task<IReadOnlyList<Versioned>> FindByPendingStatusAsync(string status, int max, CancellationToken token);

    /// <summary>A sweep's read, without steps: a row from here is never written back whole.</summary>
    Task<IReadOnlyList<Versioned>> FindSentBeforeAsync(DateTimeOffset cutoff, int max, CancellationToken token);

    /// <summary>Marks each row still at its ETag as sent, and returns those. The rest moved on under another writer.</summary>
    Task<IReadOnlyList<Versioned>> MarkSentAsync(IReadOnlyList<Versioned> rows, DateTimeOffset at, CancellationToken token);
    Task<IReadOnlyList<Versioned>> FindInStateAsync(BatchState state, CancellationToken token);

    /// <summary>Null farms means every farm.</summary>
    Task<IReadOnlyList<Batch>> FindForReportsAsync(IReadOnlyList<string>? farms, CancellationToken token);

    /// <summary>Counts of every batch, and without their steps only the rows Live must show.</summary>
    Task<LiveScan> FindForLiveAsync(CancellationToken token);

    Task<bool> ReachableAsync(CancellationToken token);
}

public sealed record BatchKey(string FarmId, string Id);

/// <summary>A create when ETag is null, otherwise a replace at that ETag.</summary>
public sealed record BundleWrite(Batch Batch, string? ETag);

/// <summary>How many batches of a farm stand in a state.</summary>
public sealed record StateCount(string FarmId, string State, int Count);

/// <summary>What Live holds of the store: a bounded set of rows, and counts for everything else.</summary>
public sealed record LiveScan(IReadOnlyList<StateCount> Counts, IReadOnlyList<Versioned> Rows, bool Capped = false)
{
    public const int RowsPerFarm = 200;
    public const int MaxOpen = 5000;
}

public interface IHopReporter
{
    /// <summary>Never blocks and never throws: a lost hop loses a dot, nothing else.</summary>
    void Report(Hop hop);
}

public interface IChangeReporter
{
    /// <summary>Never blocks and never throws: a lost change is healed by the Live reconcile.</summary>
    void Report(Versioned row);
}

/// <summary>All a pod has to tell Live, as one post. The id lets Live know a post that came again after a lost reply.</summary>
public sealed record LiveReports(IReadOnlyList<Hop> Hops, IReadOnlyList<Versioned> Changes, IReadOnlyList<ServiceLogEntry> Logs, string? PostId = null)
{
    public int Count => Hops.Count + Changes.Count + Logs.Count;
}

public interface IBusProbe
{
    Task<bool> ReachableAsync(CancellationToken token);
}

public sealed record Hop(
    string TraceId, string BatchId, string FarmId,
    [property: System.Text.Json.Serialization.JsonPropertyName("hop")] string Name,
    string State, DateTimeOffset At, bool Ok,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Pod = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? CorrelationId = null);

public static class HopNames
{
    public const string Api = "api";
    public const string Dispatcher = "dispatcher";
    public const string Bus = "bus";
    public const string Deliver = "deliver";
    public const string Worker = "worker";
    public const string Record = "record";
}

public sealed record BatchEvent(string EventId, string BatchId, string FarmId, string TraceId, string State, string? CorrelationId = null);
