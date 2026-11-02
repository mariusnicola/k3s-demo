using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BatchTracker.IntegrationTests;

public class CorrelationChainTests(Stack stack) : StackTest(stack)
{
    /// <summary>Records the id on every request a client sends.</summary>
    private sealed class Watch(HttpMessageHandler? inner = null) : DelegatingHandler(inner ?? new HttpClientHandler())
    {
        public ConcurrentQueue<string?> Sent { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sent.Enqueue(request.Headers.TryGetValues(Correlation.Header, out var ids) ? Assert.Single(ids) : null);
            return base.SendAsync(request, token);
        }
    }

    /// <summary>Answers every call, so a client can be wired up with nothing behind it.</summary>
    private sealed class Answer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    /// <summary>Line host whose calls to the API are watched.</summary>
    private static LineHost WatchedLine(Stack stack, Watch[] watch) => new(stack.Api, new LineOptions { EventTimeout = TimeSpan.FromMilliseconds(200) }, handler =>
    {
        watch[0] = new Watch(handler);
        return watch[0];
    });

    private static IConfiguration Settings() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["WORKER_URL"] = "http://worker/",
        ["INTERNAL_TOKEN"] = "token",
        ["LIVE_URL"] = "http://live/"
    }).Build();

    [Fact]
    public async Task REQ_BT_052_A_call_to_the_Worker_carries_the_current_id()
    {
        var watch = new Watch(new Answer());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkerClient(Settings());
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => watch));
        await using var provider = services.BuildServiceProvider();
        using var action = Correlation.Use("to-worker");

        await provider.GetRequiredService<IWorkerClient>().ReadyAsync(default);

        Assert.Equal("to-worker", Assert.Single(watch.Sent));
    }

    [Fact]
    public async Task REQ_BT_052_A_hop_on_the_wire_carries_the_current_id()
    {
        var sent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new Capture(sent);
        var reporter = new ReportBuffer();
        using var action = Correlation.Use("on-hop");

        reporter.Report(new Hop("t", "b", "farm-a", HopNames.Api, "Created", DateTimeOffset.UtcNow, true));
        await new ReportFlusher(new HttpClient(capture) { BaseAddress = new Uri("http://live/") }, reporter).FlushAsync(default);

        var hop = JsonDocument.Parse(await sent.Task.WaitAsync(TimeSpan.FromSeconds(10))).RootElement.GetProperty("hops")[0];
        Assert.Equal("on-hop", hop.GetProperty(Correlation.Key).GetString());
    }

    /// <summary>Keeps the first body sent.</summary>
    private sealed class Capture(TaskCompletionSource<string> body) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            body.TrySetResult(await request.Content!.ReadAsStringAsync(token));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    [Fact]
    public async Task REQ_BT_052_A_bus_message_carries_the_id_on_each_of_its_events()
    {
        await using var dispatcher = new DispatcherHost(_ => { });
        await using var queue = await dispatcher.Services.GetRequiredService<IEventQueueFactory>().ConnectAsync(default);
        await queue.SendAsync([new BatchEvent("e", "b", "farm-a", "t", "Created", "on-bus")], default);
        await using var receiver = Stack.Bus.CreateReceiver(Env.Queue);

        var message = await receiver.PeekMessageAsync();

        Assert.Equal("on-bus", Assert.Single(message.Body.ToObjectFromJson<BatchEvent[]>(JsonDefaults.Options)!).CorrelationId);
    }

    [Fact]
    public async Task REQ_BT_052_One_walk_has_one_id_on_its_calls_and_its_entries()
    {
        var watch = new Watch[1];
        await using var line = WatchedLine(Stack, watch);
        line.CreateClient();
        var engine = line.Services.GetRequiredService<LineEngine>();

        await engine.RunBatchAsync(1, CancellationToken.None);
        await engine.RunBatchAsync(2, CancellationToken.None);

        var entries = line.Services.GetRequiredService<LineLog>().Query(new LogQuery()).Entries;
        var first = entries.Where(e => e.BatchNumber == 1).Select(e => e.CorrelationId).Distinct().ToList();
        var second = entries.Where(e => e.BatchNumber == 2).Select(e => e.CorrelationId).Distinct().ToList();
        Assert.True(Correlation.IsValid(Assert.Single(first)));
        Assert.NotEqual(first[0], Assert.Single(second));
        Assert.Equal(first.Concat(second).Order(), watch[0].Sent.Distinct().Select(i => i!).Order());
    }

    [Fact]
    public async Task REQ_BT_052_The_log_route_filters_by_id()
    {
        await using var line = new LineHost(Stack.Api, new LineOptions { EventTimeout = TimeSpan.FromMilliseconds(200) });
        var client = line.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Sockets.TokenFor("ada"));
        await line.Services.GetRequiredService<LineEngine>().RunBatchAsync(1, CancellationToken.None);
        await line.Services.GetRequiredService<LineEngine>().RunBatchAsync(2, CancellationToken.None);
        var id = line.Services.GetRequiredService<LineLog>().Query(new LogQuery(Batch: "1")).Entries[0].CorrelationId;

        var page = await client.GetFromJsonAsync<JsonElement>($"/line/logs?correlationId={id}", JsonDefaults.Options);

        Assert.All(page.GetProperty("entries").EnumerateArray(), e => Assert.Equal(1, e.GetProperty("batchNumber").GetInt32()));
        Assert.NotEqual(0, page.GetProperty("matched").GetInt32());
    }
}
