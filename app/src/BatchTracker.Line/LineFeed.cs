using System.Security.Claims;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Infrastructure;

namespace BatchTracker.Line;

/// <summary>Pushes the line's status and new log entries, so the page needs no polling.</summary>
public sealed class LineFeed(LineEngine engine, LineLog log, TimeProvider time) : BackgroundService
{
    public const int MaxEntries = 500;
    public const int SnapshotEntries = 200;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private sealed class Subscriber(SocketClient client, long cursor, string status)
    {
        public SocketClient Client { get; } = client;
        public long Cursor { get; set; } = cursor;
        public string Status { get; set; } = status;
    }

    private readonly object sync = new();
    private readonly List<Subscriber> subscribers = [];

    public SocketClient Connect(Caller caller)
    {
        var client = new SocketClient(caller);
        lock (sync)
        {
            var status = engine.Status();
            var tail = log.Since(0, SnapshotEntries);
            client.TrySend(JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "snapshot",
                at = time.GetUtcNow(),
                status,
                entries = tail.Entries,
                entriesDropped = tail.Dropped,
                evicted = tail.Evicted
            }, JsonDefaults.Options));
            subscribers.Add(new Subscriber(client, tail.Cursor, Json(status)));
        }
        return client;
    }

    public void Disconnect(SocketClient client)
    {
        lock (sync) subscribers.RemoveAll(s => s.Client == client);
        client.Drop();
    }

    public void Publish()
    {
        lock (sync)
        {
            var status = engine.Status();
            var text = Json(status);
            foreach (var subscriber in subscribers.ToList())
            {
                var tail = log.Since(subscriber.Cursor, MaxEntries);
                if (tail.Entries.Count == 0 && text == subscriber.Status) continue;
                subscriber.Cursor = tail.Cursor;
                subscriber.Status = text;

                var frame = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "line",
                    at = time.GetUtcNow(),
                    status,
                    entries = tail.Entries,
                    entriesDropped = tail.Dropped,
                    evicted = tail.Evicted
                }, JsonDefaults.Options);
                if (subscriber.Client.TrySend(frame)) continue;
                subscribers.Remove(subscriber);
                subscriber.Client.Drop();
            }
        }
    }

    public static Task RunAsync(HttpContext context, ClaimsPrincipal user, LineFeed feed) =>
        SocketSession.RunAsync(context, user, feed.Connect, feed.Disconnect);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken)) Publish();
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);
}
