using System.Text.Json;

/// <summary>
/// Reads the throttle count the WSL report takes from the emulator's log. Only the distro can read that log.
/// A failed check, so a bus holding work back reads red for as long as it lasts.
/// </summary>
public static class BusThrottle
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(180);
    private const string Name = "service bus throttle";
    private const string NoReading = "no reading of the bus emulator's throttling";

    /// <summary>The report check for the file at this path.</summary>
    public static Check Check(string path, DateTimeOffset now)
    {
        var problem = Problem(Read(path), now);
        return new Check(Name, problem is null, problem);
    }

    /// <summary>Why this report does not show a bus free of throttling, or null when it does.</summary>
    public static string? Problem(string? text, DateTimeOffset now)
    {
        if (text is null || Parse(text) is not { } report) return NoReading;
        if (!report.TryGetProperty("at", out var at) || !at.TryGetDateTimeOffset(out var written)) return NoReading;
        if (!report.TryGetProperty("busThrottled", out var count) || !count.TryGetInt32(out var lines)) return NoReading;
        var age = now - written;
        if (age > MaxAge) return $"{NoReading}: the report is {(int)age.TotalSeconds} s old";
        return lines == 0 ? null : $"the bus emulator throttled {lines} times in 60 s, error 50002";
    }

    private static string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static JsonElement? Parse(string text)
    {
        try
        {
            return JsonDocument.Parse(text).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
