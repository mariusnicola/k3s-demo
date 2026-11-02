using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>UpstreamStatus is nginx's own text, null on a row from before the field.</summary>
public record IngressRow(DateTimeOffset At, int Status, string? UpstreamStatus, string Path);
public record IngressLogReply(string CorrelationId, bool Available, DateTimeOffset? Since, IngressRow[] Rows);

/// <summary>The newest ingress rows with a correlation id. A client writes that header, so a row shows a request claimed the id, nothing more.</summary>
public sealed class IngressLog(int capacity)
{
    /// <summary>Every request writes a row, so what is held has a fixed end.</summary>
    public const int KeptRows = 5000;
    public const int MaxPath = 200;
    public const int MaxLine = 2048;

    private const int MaxId = 64;

    private readonly Queue<(string Id, IngressRow Row)> rows = new();
    private DateTimeOffset? newest;
    private volatile bool reading;

    public bool Reading => reading;

    /// <summary>The oldest time still held, so an empty answer about something older reads as unknown.</summary>
    public DateTimeOffset? Since
    {
        get
        {
            lock (rows) return rows.Count == 0 ? null : rows.Peek().Row.At;
        }
    }

    public static bool IsId(string? id) => id is { Length: > 0 and <= MaxId } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public IngressRow[] Rows(string id)
    {
        lock (rows) return rows.Where(r => r.Id == id).Select(r => r.Row).Distinct().OrderByDescending(r => r.At).ToArray();
    }

    /// <summary>Anything but a row in the set format is skipped: the controller also writes its own lines to this stream.</summary>
    public void Add(string line)
    {
        if (line.Length > MaxLine || Parse(line) is not { } parsed) return;
        lock (rows)
        {
            rows.Enqueue(parsed);
            if (rows.Count > capacity) rows.Dequeue();
            if (newest is null || parsed.Row.At > newest) newest = parsed.Row.At;
        }
    }

    /// <summary>
    /// One connection, until the stream ends. A reconnect asks only for the gap, and repeats go when rows are read.
    /// The first line of any kind fires receiving: an open stream that delivers nothing proves nothing.
    /// </summary>
    public async Task Follow(Func<int?, Task<Stream>> open, CancellationToken token, Action? receiving = null)
    {
        int? gap;
        lock (rows) gap = newest is null ? null : (int)Math.Ceiling((DateTimeOffset.UtcNow - newest.Value).TotalSeconds) + 2;
        using var stream = await open(gap);
        using var lines = new StreamReader(stream);
        reading = true;
        try
        {
            while (await lines.ReadLineAsync(token) is { } line)
            {
                Add(line);
                receiving?.Invoke();
                receiving = null;
            }
        }
        finally
        {
            reading = false;
        }
    }

    private static (string Id, IngressRow Row)? Parse(string line)
    {
        if (!line.StartsWith('{')) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("time", out var time) || !root.TryGetProperty("id", out var id) || !root.TryGetProperty("status", out var status) || !root.TryGetProperty("path", out var path)) return null;
            if (id.ValueKind != JsonValueKind.String || !IsId(id.GetString()) || path.ValueKind != JsonValueKind.String || status.ValueKind != JsonValueKind.Number) return null;
            if (!DateTimeOffset.TryParse(time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return null;
            var clean = path.GetString()!.Split('?')[0];
            var upstream = root.TryGetProperty("upstream", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return (id.GetString()!, new IngressRow(at, status.GetInt32(), upstream, clean.Length > MaxPath ? clean[..MaxPath] : clean));
        }
        catch (Exception problem) when (problem is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}

public static class IngressRoutes
{
    /// <summary>The session is checked first, as on /metrics/series. Not reading is 503: an empty list would say the request never arrived.</summary>
    public static void Map(WebApplication app, SessionReader sessions, IngressLog log, ProbeLimits limits)
    {
        app.MapGet("/ingress/logs", async (HttpContext context) =>
        {
            if (await sessions.CookieAdminUntil(context.Request) is null)
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign in again", detail: "An admin session is required.");

            string? id = context.Request.Query["correlationId"];
            if (!IngressLog.IsId(id))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown id", detail: "Send one correlationId of letters, digits and hyphens, at most 64.");

            var available = log.Reading;
            var reply = new IngressLogReply(id!, available, log.Since, available ? log.Rows(id!) : []);
            return Results.Json(reply, new JsonSerializerOptions(JsonSerializerDefaults.Web), statusCode: available ? 200 : 503);
        }).AddEndpointFilter(limits);
    }
}
