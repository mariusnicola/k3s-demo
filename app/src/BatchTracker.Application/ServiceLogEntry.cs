namespace BatchTracker.Application;

/// <summary>The values a service log entry may hold.</summary>
public static class ServiceLogLevel
{
    public const string Warning = "warning";
    public const string Error = "error";
    public const string WarningReason = "service-warning";
    public const string ErrorReason = "service-error";
    public const string BrowserReason = "browser-failure";
    public const string BrowserService = "browser";

    public static readonly IReadOnlyList<string> All = [Warning, Error];
    public static readonly IReadOnlyList<string> Reasons = [WarningReason, ErrorReason, BrowserReason];
    public static readonly IReadOnlyList<string> Services = ["api", "worker", "dispatcher", "live", BrowserService];
}

/// <summary>A warning or error from a service, in the fields the Line log uses so the page can merge both.</summary>
public sealed record ServiceLogEntry(
    DateTimeOffset At,
    string Level,
    string Reason,
    string Message,
    string Service,
    string Pod,
    string? CorrelationId,
    string? ExceptionType,
    string? ExceptionMessage,
    string? TraceId = null,
    long? BatchNumber = null,
    string? BatchId = null,
    string? Farm = null,
    string? Step = null,
    long? WaitedMs = null,
    int? HttpStatus = null,
    string? ReportId = null,
    string? Kind = null,
    string? Route = null)
{
    public string Impact => Reason == ServiceLogLevel.ErrorReason
        ? "The action this entry belongs to may not have finished."
        : "The service carried on.";

    public string NextAction => "Find this correlation ID in the other entries to see the whole action.";
}

/// <summary>Where a service's warnings and errors go: a call to Live, or Live's own list.</summary>
public interface ILogSink
{
    /// <summary>Never blocks and never throws: a lost entry still sits in the pod's own log.</summary>
    void Add(ServiceLogEntry entry);
}
