using System.Text.Json;

/// <summary>What the watcher on Windows wrote: when, and each of its checks.</summary>
public sealed record HostFile(DateTimeOffset CheckedAt, Check[]? Checks);

/// <summary>
/// Reads the watcher's file, so the disk and WSL show on the site while it is up.
/// The watcher runs every minute: a file past the limit means it died, and each side then sees the other go.
/// </summary>
public static class HostReport
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(180);
    private const string Name = "host";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The report check for the file at this path.</summary>
    public static Check Check(string path, DateTimeOffset now)
    {
        var problem = Problem(Read(path), now);
        return new Check(Name, problem is null, problem);
    }

    /// <summary>Why this file is not a clean report, or null when it is.</summary>
    public static string? Problem(string? text, DateTimeOffset now)
    {
        if (text is null) return "the Windows watcher has not reported";
        var file = Parse(text);
        if (file is null) return "the Windows watcher's report is unreadable";
        var age = now - file.CheckedAt;
        if (age > MaxAge) return $"the Windows watcher last reported {(int)age.TotalSeconds} s ago";
        if (file.Checks is not { Length: > 0 }) return "the Windows watcher reported no checks";
        var failing = file.Checks.Where(c => !c.Healthy).Select(c => $"{c.Name}: {c.Error}").ToList();
        return failing.Count == 0 ? null : string.Join("; ", failing);
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

    private static HostFile? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<HostFile>(text, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
