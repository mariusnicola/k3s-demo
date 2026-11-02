using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace BatchTracker.Infrastructure;

/// <summary>The built-in console prints exception text as it came, so the pod log needs its own line writer to mask it.</summary>
public sealed class RedactedConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "redacted";

    /// <summary>One masked entry: level, category, id scope and message on a line, then the exception and its stack.</summary>
    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var line = new StringBuilder($"{Tag(logEntry.LogLevel)}: {logEntry.Category}[{logEntry.EventId.Id}]");
        scopeProvider?.ForEachScope((scope, text) => text.Append(" => ").Append(scope), line);
        line.Append(' ').Append(logEntry.Formatter(logEntry.State, logEntry.Exception));
        if (logEntry.Exception is not null) line.AppendLine().Append(logEntry.Exception);
        textWriter.WriteLine(Secrets.Redact(line.ToString()));
    }

    /// <summary>The four letters the built-in console prints, so the line keeps its shape.</summary>
    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        _ => "crit"
    };
}
