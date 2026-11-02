using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace BatchTracker.UnitTests;

/// <summary>Everything a pod tells Live waits in one bounded buffer and goes as one post, so a burst costs no request per item.</summary>
public class LiveReportTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class Live(HttpStatusCode answer = HttpStatusCode.Accepted) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return new HttpResponseMessage(answer);
        }
    }

    private static Hop AHop(string trace = "t") => new(trace, "b", "farm-a", HopNames.Api, "Created", Noon, true);

    private static Versioned ARow() => new(BatchMachine.Create("b", "farm-a", "p", new Stamp("u", "t", Noon)), "etag");

    private static ServiceLogEntry AnEntry() =>
        new(Noon, ServiceLogLevel.Error, ServiceLogLevel.ErrorReason, "It failed.", "api", "api-1", "id-1", null, null);

    private static ReportFlusher Flusher(Live live, ReportBuffer buffer) =>
        new(new HttpClient(live) { BaseAddress = new Uri("http://live/") }, buffer);

    [Fact]
    public async Task REQ_BT_050_Hops_rows_and_log_entries_go_as_one_post()
    {
        var live = new Live();
        var buffer = new ReportBuffer();
        buffer.Report(AHop("one"));
        buffer.Report(AHop("two"));
        buffer.Report(ARow());
        buffer.Add(AnEntry());

        await Flusher(live, buffer).FlushAsync(default);

        Assert.Equal("/reports", Assert.Single(live.Paths));
        using var body = JsonDocument.Parse(live.Bodies[0]);
        Assert.Equal(["one", "two"], body.RootElement.GetProperty("hops").EnumerateArray().Select(h => h.GetProperty("traceId").GetString()));
        Assert.Single(body.RootElement.GetProperty("changes").EnumerateArray());
        Assert.Single(body.RootElement.GetProperty("logs").EnumerateArray());
    }

    [Fact]
    public async Task REQ_BT_050_An_empty_buffer_posts_nothing()
    {
        var live = new Live();

        await Flusher(live, new ReportBuffer()).FlushAsync(default);

        Assert.Empty(live.Paths);
    }

    [Fact]
    public void REQ_BT_050_Every_post_has_an_id_of_its_own()
    {
        var buffer = new ReportBuffer();

        Assert.NotEqual(buffer.Drain().PostId, buffer.Drain().PostId);
    }

    [Fact]
    public void REQ_BT_050_Past_the_cap_a_report_is_dropped_and_counted()
    {
        var buffer = new ReportBuffer(cap: 2);

        buffer.Report(AHop());
        buffer.Report(ARow());
        buffer.Add(AnEntry());

        Assert.Equal(1, buffer.Dropped);
    }

    [Fact]
    public async Task REQ_BT_050_A_flush_frees_the_cap()
    {
        var buffer = new ReportBuffer(cap: 1);
        buffer.Report(AHop());
        await Flusher(new Live(), buffer).FlushAsync(default);

        buffer.Report(AHop());

        Assert.Equal(0, buffer.Dropped);
    }

    [Fact]
    public async Task REQ_BT_050_A_post_that_has_failed_for_good_takes_its_reports_with_it()
    {
        var live = new Live(HttpStatusCode.ServiceUnavailable);
        var buffer = new ReportBuffer();
        buffer.Report(AHop());
        var flusher = Flusher(live, buffer);

        await flusher.FlushAsync(default);
        await flusher.FlushAsync(default);

        Assert.Single(live.Paths);
    }

    [Fact]
    public async Task REQ_BT_050_A_live_that_cannot_be_reached_throws_nothing()
    {
        var buffer = new ReportBuffer();
        buffer.Report(AHop());

        await new ReportFlusher(new HttpClient(new Unreachable()) { BaseAddress = new Uri("http://live/") }, buffer).FlushAsync(default);
    }

    /// <summary>Always 503, and it notes what each attempt carried and how many ran together.</summary>
    private sealed class Down : HttpMessageHandler
    {
        private int running;
        private int mostAtOnce;

        public ConcurrentQueue<string> Bodies { get; } = new();
        public int MostAtOnce => Volatile.Read(ref mostAtOnce);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(now);
            Bodies.Enqueue(await request.Content!.ReadAsStringAsync(token));
            await Task.Delay(20, token);
            Interlocked.Decrement(ref running);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        private void InterlockedMax(int value)
        {
            int seen;
            do seen = Volatile.Read(ref mostAtOnce);
            while (value > seen && Interlocked.CompareExchange(ref mostAtOnce, value, seen) != seen);
        }
    }

    [Fact]
    public async Task REQ_BT_050_While_a_post_retries_it_stays_the_only_one_and_the_cap_still_drops()
    {
        var live = new Down();
        var buffer = new ReportBuffer(cap: 2);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(buffer);
        services.AddHttpClient<ReportFlusher>(h => h.BaseAddress = new Uri("http://live/")).AddRetries()
            .ConfigurePrimaryHttpMessageHandler(() => live);
        await using var provider = services.BuildServiceProvider();
        var flusher = provider.GetRequiredService<ReportFlusher>();
        buffer.Report(AHop("first"));
        await flusher.StartAsync(default);
        await UntilAsync(() => !live.Bodies.IsEmpty);

        foreach (var trace in new[] { "second", "third", "fourth" }) buffer.Report(AHop(trace));
        await UntilAsync(() => live.Bodies.Count >= 3);
        await flusher.StopAsync(new CancellationTokenSource(TimeSpan.Zero).Token);

        Assert.True(live.Bodies.Count >= 3, $"{live.Bodies.Count} attempts");
        Assert.Equal(1, live.MostAtOnce);
        Assert.All(live.Bodies.Take(3), body => Assert.Contains("first", body));
        Assert.Equal(1, buffer.Dropped);
    }

    private static async Task UntilAsync(Func<bool> done)
    {
        var start = Stopwatch.StartNew();
        while (!done() && start.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new HttpRequestException("refused");
    }

    [Fact]
    public async Task REQ_BT_052_A_hop_carries_the_pod_and_the_id_of_the_moment_it_was_reported()
    {
        var live = new Live();
        var buffer = new ReportBuffer(source: new HopSource("api-7"));
        using (Correlation.Use("then")) buffer.Report(AHop());

        using (Correlation.Use("later")) await Flusher(live, buffer).FlushAsync(default);

        using var body = JsonDocument.Parse(live.Bodies[0]);
        var hop = body.RootElement.GetProperty("hops")[0];
        Assert.Equal("api-7", hop.GetProperty("pod").GetString());
        Assert.Equal("then", hop.GetProperty("correlationId").GetString());
    }

    [Fact]
    public void REQ_BT_028_A_saved_write_reports_the_api_hop_and_no_store_hop()
    {
        var hops = new FakeHops();
        var batch = Batches.In(BatchState.Created, "b") with { Pending = new PendingEvent { EventId = "e", TraceId = "t", Status = PendingStatus.Pending } };

        new BatchWriteNews(hops, TimeProvider.System, new FakeCounters()).Created(batch);

        Assert.Equal([HopNames.Api], hops.Hops.Select(h => h.Name));
    }
}
