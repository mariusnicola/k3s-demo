using System.Collections.Concurrent;
using System.Net.Http.Json;
using BatchTracker.Application;
using Microsoft.Extensions.Hosting;

namespace BatchTracker.Infrastructure;

/// <summary>
/// All a pod tells Live waits here, so a report never waits on Live and a burst costs one post. Past the cap a report is
/// dropped. The reconcile puts a row back within a minute. A hop or a log entry stays lost to Live, and a log entry stays in the pod log.
/// </summary>
public sealed class ReportBuffer(int cap = ReportBuffer.DefaultCap, HopSource? source = null) : IHopReporter, IChangeReporter, ILogSink
{
    public const int DefaultCap = 1000;

    private readonly ConcurrentQueue<Hop> hops = new();
    private readonly ConcurrentQueue<Versioned> changes = new();
    private readonly ConcurrentQueue<ServiceLogEntry> logs = new();
    private int held;
    private int dropped;

    public int Dropped => Volatile.Read(ref dropped);

    /// <summary>Stamped now: by the flush the action that reported it has ended.</summary>
    public void Report(Hop hop)
    {
        if (TakePlace()) hops.Enqueue(hop with { Pod = source?.Pod, CorrelationId = Correlation.Current });
    }

    public void Report(Versioned row)
    {
        if (TakePlace()) changes.Enqueue(row);
    }

    public void Add(ServiceLogEntry entry)
    {
        if (TakePlace()) logs.Enqueue(entry);
    }

    /// <summary>Takes what is held; what arrives during the take waits for the next one.</summary>
    public LiveReports Drain()
    {
        var reports = new LiveReports(Take(hops), Take(changes), Take(logs), Guid.NewGuid().ToString("N"));
        Interlocked.Add(ref held, -reports.Count);
        return reports;
    }

    private bool TakePlace()
    {
        if (Interlocked.Increment(ref held) <= cap) return true;
        Interlocked.Decrement(ref held);
        Interlocked.Increment(ref dropped);
        return false;
    }

    private static List<T> Take<T>(ConcurrentQueue<T> queue)
    {
        var taken = new List<T>();
        for (var left = queue.Count; left > 0 && queue.TryDequeue(out var item); left--) taken.Add(item);
        return taken;
    }
}

/// <summary>
/// Posts the buffer as often as Live sends its frames. A post retries under the service rule, and the loop waits for it, so
/// a pod has one sender however many reports it holds, and what arrives meanwhile waits in the buffer up to its cap. Live
/// drops what it already holds, so a repeat is safe. The client is asked for at the first flush, because a hosted service
/// is built before the settings check runs and a bad LIVE_URL must reach that check.
/// </summary>
public sealed class ReportFlusher(Func<HttpClient> client, ReportBuffer buffer) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1);

    private readonly Lazy<HttpClient> http = new(client);

    public ReportFlusher(HttpClient http, ReportBuffer buffer) : this(() => http, buffer)
    {
    }

    public Task FlushAsync(CancellationToken token) => PostAsync(request => request.SafeToRepeat(), token);

    private async Task PostAsync(Func<HttpRequestMessage, HttpRequestMessage> attempts, CancellationToken token)
    {
        var reports = buffer.Drain();
        if (reports.Count == 0) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, "reports")
        {
            Content = JsonContent.Create(reports, options: JsonDefaults.Options)
        };
        try
        {
            using var response = await http.Value.SendAsync(attempts(request).WithAttemptTimeout(AttemptTimeout), token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await FlushAsync(stoppingToken);
    }

    /// <summary>What a stopping pod still holds goes first: its last hops and errors are the ones worth having. One attempt, so Live being away cannot hold the pod past its stop.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await PostAsync(request => request.SingleAttempt(), cancellationToken);
    }
}
