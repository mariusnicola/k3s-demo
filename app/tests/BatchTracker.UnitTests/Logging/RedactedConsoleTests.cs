using BatchTracker.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BatchTracker.UnitTests;

public class RedactedConsoleTests
{
    private const string Key = "AccountKey=abc123==;rest";

    /// <summary>The text one entry prints, with one id in scope.</summary>
    private static string Print(LogLevel level, string message, Exception? failure = null)
    {
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push("correlationId:abc-1");
        using var writer = new StringWriter();
        var entry = new LogEntry<string>(level, "BatchTracker.Test", new EventId(7), message, failure, (s, _) => s);
        new RedactedConsoleFormatter().Write(entry, scopes, writer);
        return writer.ToString();
    }

    private static Exception Thrown()
    {
        try
        {
            throw new InvalidOperationException("outer " + Key, new TimeoutException("inner " + Key));
        }
        catch (Exception e)
        {
            return e;
        }
    }

    [Fact]
    public void REQ_BT_053_A_secret_in_the_exception_is_masked_inner_included()
    {
        var line = Print(LogLevel.Error, "Store failed", Thrown());

        Assert.DoesNotContain("abc123", line);
        Assert.Equal(3, line.Split(Secrets.Mask).Length);
    }

    [Fact]
    public void REQ_BT_053_A_secret_in_the_message_is_masked()
    {
        Assert.DoesNotContain("abc123", Print(LogLevel.Warning, "Used " + Key));
    }

    [Fact]
    public void REQ_BT_053_The_line_holds_the_id_exception_and_stack()
    {
        var line = Print(LogLevel.Error, "Store failed", Thrown());

        Assert.Contains("correlationId:abc-1", line);
        Assert.Contains(nameof(InvalidOperationException), line);
        Assert.Contains(nameof(Thrown), line);
    }

    [Fact]
    public void REQ_BT_053_The_id_stays_on_the_first_line_with_the_message()
    {
        var first = Print(LogLevel.Error, "Store failed", Thrown()).Split('\n')[0];

        Assert.Contains("correlationId:abc-1", first);
        Assert.Contains("Store failed", first);
    }

    [Fact]
    public void REQ_BT_053_The_line_keeps_the_shape_of_the_old_console()
    {
        Assert.StartsWith("info: BatchTracker.Test[7] => correlationId:abc-1 Fine", Print(LogLevel.Information, "Fine"));
        Assert.StartsWith("warn: ", Print(LogLevel.Warning, "Careful"));
        Assert.StartsWith("fail: ", Print(LogLevel.Error, "Failed"));
        Assert.StartsWith("crit: ", Print(LogLevel.Critical, "Gone"));
        Assert.StartsWith("dbug: ", Print(LogLevel.Debug, "Detail"));
    }
}
