namespace BatchTracker.Line;

public static class LineLevel
{
    public const string Info = "info";
    public const string Error = "error";

    public static readonly IReadOnlyList<string> All = [Info, Error];
}

public static class LineReason
{
    public const string Started = "started";
    public const string Closed = "closed";
    public const string Rejected = "rejected";

    public const string EventTimeout = "event-timeout";
    public const string HttpRefused = "http-refused";
    public const string NetworkFailure = "network-failure";
    public const string LoginFailure = "login-failure";
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> Errors = [EventTimeout, HttpRefused, NetworkFailure, LoginFailure, Unknown];
    public static readonly IReadOnlyList<string> All = [Started, Closed, Rejected, .. Errors];
}

public sealed record LineEntry(
    DateTimeOffset At,
    string Level,
    string Reason,
    string Message,
    long BatchNumber,
    string? BatchId,
    string Farm,
    string Step,
    long WaitedMs,
    int? HttpStatus,
    string? ExceptionType,
    string? ExceptionMessage,
    string? TraceId,
    string? CorrelationId = null)
{
    public string Impact => LineWords.ImpactOf(Reason);
    public string NextAction => LineWords.NextActionOf(Reason);
}

public sealed record LogQuery(
    string? Level = null, string? Reason = null, string? Farm = null, string? Batch = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, int? Limit = null, string? CorrelationId = null);

public sealed record LogPage(
    int Matched,
    IReadOnlyDictionary<string, int> ByReason,
    int DroppedByLimit,
    long Evicted,
    IReadOnlyList<LineEntry> Entries);

public sealed record LogTail(long Cursor, int Dropped, long Evicted, IReadOnlyList<LineEntry> Entries);

public sealed record LogCounts(long Started, long Closed, long Rejected, long Errors, IReadOnlyDictionary<string, long> ErrorsByReason);

public sealed class LineLog
{
    public const int Capacity = 5000;
    public const int DefaultLimit = 200;

    private readonly object sync = new();
    private readonly Queue<LineEntry> entries = new();
    private readonly Dictionary<string, long> sinceStart = LineReason.All.ToDictionary(r => r, _ => 0L);
    private long evicted;

    public void Add(LineEntry entry)
    {
        lock (sync)
        {
            entries.Enqueue(entry);
            sinceStart[entry.Reason]++;
            if (entries.Count <= Capacity) return;
            entries.Dequeue();
            evicted++;
        }
    }

    /// <summary>Counted apart from the buffer, so totals survive after their entries have rolled out.</summary>
    public LogCounts Counts()
    {
        lock (sync)
        {
            var errors = LineReason.Errors.ToDictionary(r => r, r => sinceStart[r]);
            return new LogCounts(
                sinceStart[LineReason.Started], sinceStart[LineReason.Closed], sinceStart[LineReason.Rejected],
                errors.Values.Sum(), errors);
        }
    }

    /// <summary>Entries written after the cursor, newest first. Those already rolled out are in Evicted, not Dropped.</summary>
    public LogTail Since(long cursor, int max)
    {
        lock (sync)
        {
            var written = evicted + entries.Count;
            var available = (int)Math.Min(written - cursor, entries.Count);
            var taken = entries.Reverse().Take(Math.Min(available, max)).ToList();
            return new LogTail(written, available - taken.Count, evicted, taken);
        }
    }

    public LogPage Query(LogQuery query)

    {
        var limit = query.Limit ?? DefaultLimit;
        lock (sync)
        {
            var matched = entries.Where(e => Matches(e, query)).ToList();
            var byReason = matched.GroupBy(e => e.Reason).ToDictionary(g => g.Key, g => g.Count());
            var newestFirst = matched.AsEnumerable().Reverse().Take(limit).ToList();
            return new LogPage(matched.Count, byReason, matched.Count - newestFirst.Count, evicted, newestFirst);
        }
    }

    /// <summary>Null means valid.</summary>
    public static string? Problem(LogQuery query)
    {
        if (query.Level is not null && !LineLevel.All.Contains(query.Level))
            return $"level must be one of {string.Join(", ", LineLevel.All)}.";
        if (query.Reason is not null && !LineReason.All.Contains(query.Reason))
            return $"reason must be one of {string.Join(", ", LineReason.All)}.";
        if (query.Limit is < 1 or > Capacity) return $"limit must be 1 to {Capacity}.";
        if (query.From > query.To) return "from must not be after to.";
        return null;
    }

    private static bool Matches(LineEntry entry, LogQuery query) =>
        (query.Level is null || entry.Level == query.Level) &&
        (query.Reason is null || entry.Reason == query.Reason) &&
        (query.Farm is null || entry.Farm == query.Farm) &&
        (string.IsNullOrWhiteSpace(query.CorrelationId) || entry.CorrelationId == query.CorrelationId.Trim()) &&
        (query.Batch is null || entry.BatchId == query.Batch || entry.BatchNumber.ToString() == query.Batch) &&
        (query.From is null || entry.At >= query.From) &&
        (query.To is null || entry.At <= query.To);
}

