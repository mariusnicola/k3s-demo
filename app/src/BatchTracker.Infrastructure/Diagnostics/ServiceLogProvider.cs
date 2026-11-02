using System.Diagnostics;
using BatchTracker.Application;
using Microsoft.Extensions.Logging;

namespace BatchTracker.Infrastructure;

/// <summary>Sends each warning and error of a service to the sink, so the Logs page sees more than the Line.</summary>
public sealed class ServiceLogProvider(Func<ILogSink> resolve, string service, string pod) : ILoggerProvider
{
    public const int MaxMessage = 2000;

    /// <summary>These log when Live is away, and reporting that to Live would only add to it.</summary>
    private static readonly string[] Quiet = ["System.Net.Http.", "BatchTracker.Infrastructure.RetryHandler"];

    [ThreadStatic]
    private static bool reporting;

    private ILogSink? sink;

    /// <summary>A writer that stays silent for the categories that would report on the reporting.</summary>
    public ILogger CreateLogger(string categoryName) => new Writer(this, !Quiet.Any(categoryName.StartsWith));

    /// <summary>Holds nothing of its own to release.</summary>
    public void Dispose()
    {
    }

    /// <summary>The guard stops a log written while reporting from reporting again.</summary>
    private void Report(LogLevel level, string message, Exception? failure)
    {
        if (reporting) return;
        reporting = true;
        try
        {
            (sink ??= resolve()).Add(EntryFor(level, message, failure));
        }
        finally
        {
            reporting = false;
        }
    }

    /// <summary>Masked and cut, with the id, trace and batch of the action under way.</summary>
    private ServiceLogEntry EntryFor(LogLevel level, string message, Exception? failure)
    {
        var error = level >= LogLevel.Error;
        return new ServiceLogEntry(
            DateTimeOffset.UtcNow,
            error ? ServiceLogLevel.Error : ServiceLogLevel.Warning,
            error ? ServiceLogLevel.ErrorReason : ServiceLogLevel.WarningReason,
            Secrets.MaskAndCut(message, MaxMessage), service, pod, Correlation.Current,
            failure?.GetType().Name,
            failure is null ? null : Secrets.MaskAndCut(failure.Message, MaxMessage),
            Activity.Current?.TraceId.ToString(),
            BatchId: Correlation.BatchId,
            Farm: Correlation.Farm,
            Step: Correlation.Step);
    }

    /// <summary>Passes warnings and errors on, and nothing else.</summary>
    private sealed class Writer(ServiceLogProvider provider, bool reports) : ILogger
    {
        /// <summary>The id comes from the current action, so scopes add nothing.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>True for warnings and above, in a category that reports.</summary>
        public bool IsEnabled(LogLevel logLevel) => reports && logLevel >= LogLevel.Warning && logLevel != LogLevel.None;

        /// <summary>Hands an enabled entry to the provider.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Report(logLevel, formatter(state, exception), exception);
        }
    }
}
