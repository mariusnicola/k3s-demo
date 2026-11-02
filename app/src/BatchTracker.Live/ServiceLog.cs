using BatchTracker.Application;
using BatchTracker.Infrastructure;

namespace BatchTracker.Live;

/// <summary>The filters of the log route. Null means no filter.</summary>
public sealed record ServiceLogQuery(
    string? Level = null, string? Service = null, string? CorrelationId = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, int? Limit = null,
    string? Reason = null, string? Farm = null, string? Batch = null, string? ReportId = null);

/// <summary>One reply of the log route: the counts before the limit, then the entries newest first.</summary>
public sealed record ServiceLogPage(int Matched, int DroppedByLimit, long Evicted, long BrowserEvicted, IReadOnlyList<ServiceLogEntry> Entries);

/// <summary>Recent warnings and errors of every service, in memory and bounded like the Line log.</summary>
public sealed class ServiceLog : ILogSink
{
    public const int Capacity = 5000;
    public const int BrowserCapacity = 1000;
    public const int DefaultLimit = 200;
    public const int MaxField = 200;
    public const int MaxEntryText = 2 * ServiceLogProvider.MaxMessage + 7 * MaxField;

    private readonly object sync = new();
    private readonly Queue<ServiceLogEntry> entries = new();
    private readonly Queue<ServiceLogEntry> browserEntries = new();
    private readonly HashSet<string> postsSeen = [];
    private readonly Queue<string> postOrder = new();
    private long evicted;
    private long browserEvicted;

    /// <summary>Stores the entry cleaned. Anyone can post a browser report, so those rows get a queue of their own and never push out a service row.</summary>
    public void Add(ServiceLogEntry entry)
    {
        var clean = Clean(entry);
        lock (sync)
        {
            if (clean.Service == ServiceLogLevel.BrowserService) browserEvicted += Hold(browserEntries, clean, BrowserCapacity);
            else evicted += Hold(entries, clean, Capacity);
        }
    }

    /// <summary>Enough for every post of every pod over the longest retry, so a repeat always finds its first time here.</summary>
    public const int RememberedPosts = 20000;

    /// <summary>Adds what a report post carried. A post whose reply was lost comes again whole; Live drops the hops and rows it holds, so only entries need this.</summary>
    public void AddPost(string? postId, IEnumerable<ServiceLogEntry> posted)
    {
        if (!FirstTime(postId)) return;
        foreach (var entry in posted) Add(entry);
    }

    private bool FirstTime(string? postId)
    {
        if (postId is null) return true;
        lock (sync)
        {
            if (!postsSeen.Add(postId)) return false;
            postOrder.Enqueue(postId);
            if (postOrder.Count > RememberedPosts) postsSeen.Remove(postOrder.Dequeue());
            return true;
        }
    }

    /// <summary>Queues the entry and answers how many left at the cap.</summary>
    private static int Hold(Queue<ServiceLogEntry> queue, ServiceLogEntry entry, int cap)
    {
        queue.Enqueue(entry);
        if (queue.Count <= cap) return 0;
        queue.Dequeue();
        return 1;
    }

    /// <summary>Keeps the limit of each queue, in the order given: a cap on the merged list would let a report flood fill the page and hide service rows.</summary>
    private static List<ServiceLogEntry> LimitEachQueue(IEnumerable<ServiceLogEntry> newestFirst, int limit)
    {
        var (browser, service) = (0, 0);
        return newestFirst.Where(e => (e.Service == ServiceLogLevel.BrowserService ? ++browser : ++service) <= limit).ToList();
    }

    /// <summary>The newest matches, up to the limit of each queue.</summary>
    public ServiceLogPage Query(ServiceLogQuery query)
    {
        var limit = query.Limit ?? DefaultLimit;
        lock (sync)
        {
            var all = entries.Concat(browserEntries).OrderBy(e => e.At).ToList();
            var request = query.ReportId is null ? null : all.LastOrDefault(e => e.ReportId == query.ReportId)?.CorrelationId;
            var matched = all.Where(e => Matches(e, query) && OfReport(e, query.ReportId, request)).ToList();
            var newestFirst = LimitEachQueue(matched.AsEnumerable().Reverse(), limit);
            return new ServiceLogPage(matched.Count, matched.Count - newestFirst.Count, evicted, browserEvicted, newestFirst);
        }
    }

    /// <summary>Null means valid.</summary>
    public static string? Problem(ServiceLogEntry entry)
    {
        if (!ServiceLogLevel.All.Contains(entry.Level)) return $"level must be one of {string.Join(", ", ServiceLogLevel.All)}.";
        if (!ServiceLogLevel.Reasons.Contains(entry.Reason)) return $"reason must be one of {string.Join(", ", ServiceLogLevel.Reasons)}.";
        if (!ServiceLogLevel.Services.Contains(entry.Service)) return $"service must be one of {string.Join(", ", ServiceLogLevel.Services)}.";
        if (string.IsNullOrWhiteSpace(entry.Message)) return "message is required.";
        if (string.IsNullOrWhiteSpace(entry.Pod)) return "pod is required.";
        return null;
    }

    /// <summary>Null means valid.</summary>
    public static string? Problem(ServiceLogQuery query)
    {
        if (query.Level is not null && !ServiceLogLevel.All.Contains(query.Level))
            return $"level must be one of {string.Join(", ", ServiceLogLevel.All)}.";
        if (query.Reason is not null && !ServiceLogLevel.Reasons.Contains(query.Reason))
            return $"reason must be one of {string.Join(", ", ServiceLogLevel.Reasons)}.";
        if (query.Limit is < 1 or > Capacity) return $"limit must be 1 to {Capacity}.";
        if (query.From > query.To) return "from must not be after to.";
        return null;
    }

    /// <summary>A report reaches its own row and every row of the request it failed on.</summary>
    private static bool OfReport(ServiceLogEntry entry, string? reportId, string? request) =>
        reportId is null || entry.ReportId == reportId || (request is not null && entry.CorrelationId == request);

    /// <summary>The reporter already masks and cuts. Holding the entry safe is still the job of whoever holds it.</summary>
    private static ServiceLogEntry Clean(ServiceLogEntry entry) => entry with
    {
        Message = Secrets.MaskAndCut(entry.Message, ServiceLogProvider.MaxMessage),
        ExceptionMessage = Text(entry.ExceptionMessage, ServiceLogProvider.MaxMessage),
        Pod = Secrets.MaskAndCut(entry.Pod, MaxField),
        CorrelationId = Text(entry.CorrelationId, MaxField),
        ExceptionType = Text(entry.ExceptionType, MaxField),
        TraceId = Text(entry.TraceId, MaxField),
        BatchId = Text(entry.BatchId, MaxField),
        Farm = Text(entry.Farm, MaxField),
        Step = Text(entry.Step, MaxField),
        ReportId = Text(entry.ReportId, MaxField),
        Kind = Text(entry.Kind, MaxField),
        Route = Text(entry.Route, MaxField)
    };

    /// <summary>Masks and cuts text that may be absent.</summary>
    private static string? Text(string? text, int max) => text is null ? null : Secrets.MaskAndCut(text, max);

    /// <summary>True when the entry passes every filter set.</summary>
    private static bool Matches(ServiceLogEntry entry, ServiceLogQuery query) =>
        (query.Level is null || entry.Level == query.Level) &&
        (query.Reason is null || entry.Reason == query.Reason) &&
        (query.Service is null || entry.Service == query.Service) &&
        (query.Farm is null || entry.Farm == query.Farm) &&
        (query.Batch is null || entry.BatchId == query.Batch) &&
        (string.IsNullOrWhiteSpace(query.CorrelationId) || entry.CorrelationId == query.CorrelationId.Trim()) &&
        (query.From is null || entry.At >= query.From) &&
        (query.To is null || entry.At <= query.To);
}
