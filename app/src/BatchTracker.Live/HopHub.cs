using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.Live;

/// <summary>Lets test hosts find this assembly's entry point.</summary>
public sealed class LiveMarker;

/// <summary>Holds what sockets need so no socket and no hop queries the store, and sends each client its frames.</summary>
public sealed class HopHub(TimeProvider time)
{
    public const int Keep = 500;
    public const int RowsPerSnapshot = LiveScan.RowsPerFarm;

    private sealed class Subscriber(SocketClient client)
    {
        public SocketClient Client { get; } = client;
        public string? Summary { get; set; }
        public string? Alerts { get; set; }
    }

    private readonly object sync = new();
    private readonly Queue<Hop> recent = new();
    private readonly List<Hop> fresh = [];
    private readonly List<Subscriber> subscribers = [];
    private readonly LiveRows held = new();
    private bool capped;
    private DateTimeOffset? reconciledAt;
    private bool reportsChanged;

    public void Add(Hop hop)
    {
        lock (sync)
        {
            // A report whose reply was lost comes again, identical to the last field.
            if (recent.Contains(hop)) return;
            recent.Enqueue(hop);
            if (recent.Count > Keep) recent.Dequeue();
            fresh.Add(hop);
            if (fresh.Count > Keep) fresh.RemoveAt(0);
        }
    }

    public void Apply(Versioned row)
    {
        lock (sync)
        {
            if (!held.Apply(row)) return;
            reportsChanged = true;

            var frame = JsonSerializer.SerializeToUtf8Bytes(new { type = "batch", etag = row.ETag, batch = row.Batch }, JsonDefaults.Options);
            foreach (var subscriber in subscribers.ToList())
            {
                if (subscriber.Client.Caller.HasFarm(row.Batch.FarmId)) Send(subscriber, frame);
            }
        }
    }

    public SocketClient Connect(Caller caller)
    {
        var subscriber = new Subscriber(new SocketClient(caller));
        lock (sync)
        {
            var visible = held.Visible(caller);
            var (summary, alerts) = (held.SummaryFor(caller, visible), Reports.AlertsOf(visible));
            subscriber.Summary = Json(summary);
            subscriber.Alerts = Json(alerts);

            var batches = held.NewestFor(caller).Select(r => new { etag = r.ETag, batch = r.Batch }).ToList();
            subscriber.Client.TrySend(JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "snapshot",
                at = time.GetUtcNow(),
                reconciledAt,
                hops = recent.Where(h => caller.HasFarm(h.FarmId)).ToList(),
                summary,
                alerts,
                batches,
                batchesDropped = Math.Max(0, held.Total(caller) - batches.Count),
                capped
            }, JsonDefaults.Options));
            subscribers.Add(subscriber);
        }
        return subscriber.Client;
    }

    public void Disconnect(SocketClient client)
    {
        lock (sync) subscribers.RemoveAll(s => s.Client == client);
        client.Drop();
    }

    public void Tick()
    {
        lock (sync)
        {
            foreach (var subscriber in subscribers.ToList())
            {
                var visible = fresh.Where(h => subscriber.Client.Caller.HasFarm(h.FarmId)).ToList();
                if (visible.Count > 0)
                    Send(subscriber, JsonSerializer.SerializeToUtf8Bytes(new { type = "hops", hops = visible }, JsonDefaults.Options));
            }
            fresh.Clear();
        }
    }

    /// <summary>Called once a second, so a burst of writes costs one summary per client.</summary>
    public void PublishReports()
    {
        lock (sync)
        {
            if (!reportsChanged) return;
            reportsChanged = false;

            foreach (var subscriber in subscribers.ToList())
            {
                var caller = subscriber.Client.Caller;
                var visible = held.Visible(caller);
                var summary = held.SummaryFor(caller, visible);
                var alerts = Reports.AlertsOf(visible);
                if (Json(summary) is var summaryText && summaryText != subscriber.Summary)
                {
                    subscriber.Summary = summaryText;
                    Send(subscriber, JsonSerializer.SerializeToUtf8Bytes(new { type = "summary", summary, reconciledAt, capped }, JsonDefaults.Options));
                }
                if (Json(alerts) is var alertsText && alertsText != subscriber.Alerts)
                {
                    subscriber.Alerts = alertsText;
                    Send(subscriber, JsonSerializer.SerializeToUtf8Bytes(new { type = "alerts", alerts }, JsonDefaults.Options));
                }
            }
        }
    }

    /// <summary>From here each write records where it took its batch from, for the scan to settle against.</summary>
    public void BeginReconcile()
    {
        lock (sync) held.BeginReconcile();
    }

    /// <summary>
    /// Swaps in a finished scan, keeping anything written while it ran. Returns the rows a snapshot should
    /// hold in full and does not, for the caller to read one by one.
    /// </summary>
    public IReadOnlyList<Batch> FinishReconcile(LiveScan scan)
    {
        lock (sync)
        {
            var missing = held.FinishReconcile(scan);

            // The first read and a change of the cap are news even when every count is unchanged, so each client gets one summary.
            if (reconciledAt is null || capped != scan.Capped) subscribers.ForEach(s => s.Summary = null);
            capped = scan.Capped;
            reconciledAt = time.GetUtcNow();
            reportsChanged = true;
            return missing;
        }
    }

    public int FullRowsHeld
    {
        get { lock (sync) return held.FullRowsHeld; }
    }

    public int ClientCount
    {
        get { lock (sync) return subscribers.Count; }
    }

    private void Send(Subscriber subscriber, byte[] frame)
    {
        if (subscriber.Client.TrySend(frame)) return;
        subscribers.Remove(subscriber);
        subscriber.Client.Drop();
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);
}
