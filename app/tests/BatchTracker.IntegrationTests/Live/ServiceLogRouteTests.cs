using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Infrastructure;
using BatchTracker.Live;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BatchTracker.IntegrationTests;

public class ServiceLogRouteTests(Stack stack) : StackTest(stack)
{
    private const string Path = "/live/logs";

    private readonly LiveHost live = new();

    public override async Task DisposeAsync() => await live.DisposeAsync();

    /// <summary>A store whose failure carries a key, the way a Cosmos message can.</summary>
    public class KeyStore : System.Reflection.DispatchProxy
    {
        public static IBatchStore Wrap() => Create<IBatchStore, KeyStore>();

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            throw new InvalidOperationException("Failed with AccountKey=abc123;rest");
    }

    /// <summary>Holds what a host reports, in place of the call to Live.</summary>
    private sealed class Sink : ILogSink
    {
        public List<ServiceLogEntry> Entries { get; } = [];
        public void Add(ServiceLogEntry entry) => Entries.Add(entry);
    }

    private static Action<IServiceCollection> Into(Sink sink) => s => s.RemoveAll<ILogSink>().AddSingleton<ILogSink>(sink);

    private static void Warn(IServiceProvider services) =>
        services.GetRequiredService<ILoggerFactory>().CreateLogger("BatchTracker.Test").LogWarning("careful");

    private static ServiceLogEntry Entry(string id = "abc-1") => new(
        DateTimeOffset.UtcNow, ServiceLogLevel.Error, ServiceLogLevel.ErrorReason, "It failed.", "api", "api-1", id, null, null);

    [Fact]
    public async Task REQ_BT_050_A_report_post_sent_twice_logs_its_entry_once()
    {
        var post = new LiveReports([], [], [Entry("sent-twice")], "post-1");
        var client = live.CreateClient().Internal();

        await client.PostAsJsonAsync("/reports", post, JsonDefaults.Options);
        await client.PostAsJsonAsync("/reports", post, JsonDefaults.Options);

        var page = await Bearer("ada").GetFromJsonAsync<JsonElement>($"{Path}?correlationId=sent-twice", JsonDefaults.Options);
        Assert.Equal(1, page.GetProperty("matched").GetInt32());
    }

    [Fact]
    public async Task REQ_BT_056_The_route_finds_a_report_and_its_request_rows()
    {
        var log = live.Services.GetRequiredService<ServiceLog>();
        log.Add(Entry("abc-1") with { Service = ServiceLogLevel.BrowserService, Reason = ServiceLogLevel.BrowserReason, ReportId = "r-1" });
        log.Add(Entry("abc-1"));
        log.Add(Entry("abc-2"));

        var page = await Bearer("ada").GetFromJsonAsync<JsonElement>($"{Path}?reportId=r-1", JsonDefaults.Options);

        Assert.Equal(2, page.GetProperty("matched").GetInt32());
        Assert.All(page.GetProperty("entries").EnumerateArray(), e => Assert.Equal("abc-1", e.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task REQ_BT_056_The_route_counts_browser_evictions_apart_from_service_rows()
    {
        var log = live.Services.GetRequiredService<ServiceLog>();
        log.Add(Entry("real"));
        for (var i = 0; i <= ServiceLog.BrowserCapacity; i++) log.Add(Entry($"b-{i}") with { Service = ServiceLogLevel.BrowserService });

        var page = await Bearer("ada").GetFromJsonAsync<JsonElement>($"{Path}?service=api", JsonDefaults.Options);

        Assert.Equal(1, page.GetProperty("matched").GetInt32());
        Assert.Equal(0, page.GetProperty("evicted").GetInt64());
        Assert.Equal(1, page.GetProperty("browserEvicted").GetInt64());
    }

    private HttpClient Bearer(string user)
    {
        var client = live.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Sockets.TokenFor(user));
        return client;
    }

    private Task<HttpResponseMessage> PostAsync(ServiceLogEntry entry) => live.CreateClient().Internal().PostAsJsonAsync("/logs", entry, JsonDefaults.Options);

    [Fact]
    public async Task REQ_BT_053_An_entry_posted_inside_the_cluster_reads_back_with_its_words()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(Entry())).StatusCode);

        var page = await Bearer("ada").GetFromJsonAsync<JsonElement>($"{Path}?correlationId=abc-1", JsonDefaults.Options);

        var entry = Assert.Single(page.GetProperty("entries").EnumerateArray());
        Assert.Equal("api", entry.GetProperty("service").GetString());
        Assert.Equal("api-1", entry.GetProperty("pod").GetString());
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("impact").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("nextAction").GetString()));
    }

    [Fact]
    public async Task REQ_BT_053_The_list_is_for_admins_only()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await live.CreateClient().GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Bearer("olivia").GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Bearer("ian").GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Bearer("viewer").GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_053_A_bad_filter_is_a_400()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Bearer("ada").GetAsync($"{Path}?level=nope")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_053_An_unhandled_error_in_the_Api_reaches_Live_with_its_id_and_masked_detail()
    {
        await using var api = Stack.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IBatchStore>().AddSingleton(KeyStore.Wrap());
            s.AddHttpClient<ReportFlusher>().ConfigurePrimaryHttpMessageHandler(() => live.Server.CreateHandler());
        }));
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Sockets.TokenFor("ada"));
        client.DefaultRequestHeaders.Add(Correlation.Header, "from-the-api");

        Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync("/api/batches")).StatusCode);

        var log = live.Services.GetRequiredService<ServiceLog>();
        for (var i = 0; i < 100 && log.Query(new ServiceLogQuery(CorrelationId: "from-the-api")).Matched == 0; i++) await Task.Delay(50);
        var entry = Assert.Single(log.Query(new ServiceLogQuery(CorrelationId: "from-the-api")).Entries);
        Assert.Equal("api", entry.Service);
        Assert.Equal(nameof(InvalidOperationException), entry.ExceptionType);
        Assert.DoesNotContain("abc123", entry.ExceptionMessage);
        Assert.Contains(Secrets.Mask, entry.ExceptionMessage);
    }

    [Theory]
    [InlineData("/reports")]
    [InlineData("/hops")]
    [InlineData("/changes")]
    [InlineData("/logs")]
    public async Task B_013_An_internal_route_refuses_a_caller_without_the_token(string path)
    {
        var wrong = live.CreateClient();
        wrong.DefaultRequestHeaders.Add(HttpWorkerClient.TokenHeader, "wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, (await live.CreateClient().PostAsJsonAsync(path, Entry(), JsonDefaults.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.PostAsJsonAsync(path, Entry(), JsonDefaults.Options)).StatusCode);
    }

    [Fact]
    public async Task B_013_A_signed_in_user_is_no_internal_caller()
    {
        var reply = await Bearer("ada").PostAsJsonAsync("/logs", Entry(), JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
    }

    [Fact]
    public async Task B_013_A_refused_forgery_leaves_nothing_behind()
    {
        await live.CreateClient().PostAsJsonAsync("/logs", Entry("forged"), JsonDefaults.Options);

        Assert.Equal(0, live.Services.GetRequiredService<ServiceLog>().Query(new ServiceLogQuery(CorrelationId: "forged")).Matched);
    }

    [Fact]
    public async Task B_013_The_flush_sends_the_token()
    {
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        await using var api = Stack.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddHttpClient<ReportFlusher>().ConfigurePrimaryHttpMessageHandler(() => new Note(seen))));

        api.Services.GetRequiredService<ILogSink>().Add(Entry());

        for (var i = 0; i < 100 && seen.IsEmpty; i++) await Task.Delay(20);
        Assert.Equal(Env.InternalToken, seen["/reports"]);
    }

    [Fact]
    public async Task REQ_BT_050_One_post_reaches_the_hub_and_the_log_and_skips_a_bad_entry()
    {
        var entry = Entry("in-a-report");
        var hop = new Hop("in-a-report", "b", "farm-a", HopNames.Api, "Created", DateTimeOffset.UtcNow, true);

        var reply = await live.CreateClient().Internal().PostAsJsonAsync("/reports", new LiveReports([hop], [], [entry, entry with { Message = "" }]), JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
        Assert.Equal(1, live.Services.GetRequiredService<ServiceLog>().Query(new ServiceLogQuery(CorrelationId: "in-a-report")).Matched);
    }

    [Theory]
    [InlineData("""{"level":"error","reason":"service-error","service":"api","pod":"p"}""")]
    [InlineData("""{"level":"error","reason":"service-error","service":"api","message":"m"}""")]
    [InlineData("""{"level":"error","reason":"service-error","service":"api","message":"","pod":"p"}""")]
    public async Task T_017_An_entry_missing_its_text_is_a_400(string json)
    {
        var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var reply = await live.CreateClient().Internal().PostAsync("/logs", body);

        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
    }

    [Theory]
    [InlineData("nope", ServiceLogLevel.ErrorReason, "api")]
    [InlineData(ServiceLogLevel.Error, "nope", "api")]
    [InlineData(ServiceLogLevel.Error, ServiceLogLevel.ErrorReason, "nope")]
    public async Task B_013_An_entry_that_fails_its_checks_is_a_400(string level, string reason, string service)
    {
        var reply = await PostAsync(Entry() with { Level = level, Reason = reason, Service = service });

        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
    }

    /// <summary>Writes down the token each report arrived with.</summary>
    private sealed class Note(System.Collections.Concurrent.ConcurrentDictionary<string, string> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            seen[request.RequestUri!.AbsolutePath] = request.Headers.TryGetValues(HttpWorkerClient.TokenHeader, out var sent) ? sent.Single() : "none";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }

    [Fact]
    public async Task REQ_BT_053_The_Worker_reports_as_worker()
    {
        var sink = new Sink();
        await using var worker = new WorkerHost(Into(sink));

        Warn(worker.Services);

        Assert.Equal("worker", Assert.Single(sink.Entries).Service);
    }

    [Fact]
    public async Task REQ_BT_053_The_Dispatcher_reports_as_dispatcher()
    {
        var sink = new Sink();
        await using var dispatcher = new DispatcherHost(Into(sink));

        Warn(dispatcher.Services);

        Assert.Equal("dispatcher", Assert.Single(sink.Entries, e => e.Message == "careful").Service);
    }

    [Fact]
    public async Task REQ_BT_053_Live_keeps_its_own_warnings_without_a_call()
    {
        live.CreateClient();
        using var action = Correlation.Use("own-warning");

        live.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BatchTracker.Test").LogWarning("careful");

        var entry = Assert.Single(live.Services.GetRequiredService<ServiceLog>().Query(new ServiceLogQuery(CorrelationId: "own-warning")).Entries);
        Assert.Equal("live", entry.Service);
        await Task.CompletedTask;
    }
}
