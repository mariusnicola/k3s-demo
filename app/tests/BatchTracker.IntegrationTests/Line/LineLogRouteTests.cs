using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

/// <summary>Real API and real Cosmos behind the Line: each failure below is really produced, not scripted.</summary>
public sealed class LineLogRouteTests(Stack stack) : StackTest(stack)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private readonly List<IAsyncDisposable> hosts = [];

    public override async Task DisposeAsync()
    {
        foreach (var host in hosts) await host.DisposeAsync();
    }

    private LineHost NewLine(LineOptions? options = null, Func<HttpMessageHandler, HttpMessageHandler>? wrap = null)
    {
        var line = new LineHost(Stack.Api, options, wrap);
        hosts.Add(line);
        return line;
    }

    private void StartDispatcher()
    {
        var dispatcher = new DispatcherHost(services =>
        {
            services.RemoveAll<IWorkerClient>().AddSingleton(_ => Stack.Worker.NewClient());
            services.RemoveAll<DispatcherOptions>().AddSingleton(new DispatcherOptions { SweepInterval = TimeSpan.FromMilliseconds(100) });
        });
        hosts.Add(dispatcher);
        dispatcher.CreateClient();
        dispatcher.Leadership.Gain();
    }

    private static HttpClient Client(LineHost line, string? user)
    {
        var client = line.CreateClient();
        if (user is null) return client;
        var token = new TokenIssuer(Env.PrivateKey, TimeProvider.System).Issue(DemoUsers.Find(user)!);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> LogsAsync(LineHost line, string query = "") =>
        JsonDocument.Parse(await Client(line, "ada").GetStringAsync($"/line/logs{query}")).RootElement;

    private static Task RunOneAsync(LineHost line, long number = 1)
    {
        line.CreateClient();
        return line.Services.GetRequiredService<LineEngine>().RunBatchAsync(number, CancellationToken.None);
    }

    private static JsonElement OnlyError(JsonElement logs) =>
        logs.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("level").GetString() == "error");

    [Fact]
    public async Task REQ_BT_045_Real_event_timeout_with_nothing_delivering_events()
    {
        var line = NewLine(new LineOptions { EventTimeout = TimeSpan.FromSeconds(1) });

        await RunOneAsync(line);

        var entry = OnlyError(await LogsAsync(line));
        Assert.Equal("event-timeout", entry.GetProperty("reason").GetString());
        Assert.Equal("Dose1", entry.GetProperty("step").GetString());
        Assert.Contains("The batch is not failed and its event may finish later.", entry.GetProperty("message").GetString());

        // The sentence is true: the batch still exists and still waits for its event.
        var batch = await Stack.Store.GetAsync("farm-b", entry.GetProperty("batchId").GetString()!, default);
        Assert.Equal(PendingStatus.Pending, batch!.Batch.Pending!.Status);
        Assert.Equal(batch.Batch.Pending.TraceId, entry.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task REQ_BT_045_Real_refusal_from_the_api_for_a_user_without_permission()
    {
        var line = NewLine(wrap: api => new ActsAsViewer(api));

        await RunOneAsync(line);

        var entry = OnlyError(await LogsAsync(line));
        Assert.Equal("http-refused", entry.GetProperty("reason").GetString());
        Assert.Equal(403, entry.GetProperty("httpStatus").GetInt32());
        Assert.Equal("Create", entry.GetProperty("step").GetString());
    }

    [Fact]
    public async Task REQ_BT_045_Real_login_failure_for_a_wrong_machine_token()
    {
        var line = NewLine(new LineOptions { MachineToken = "wrong" });

        await RunOneAsync(line);

        var entry = OnlyError(await LogsAsync(line));
        Assert.Equal("login-failure", entry.GetProperty("reason").GetString());
        Assert.Equal(401, entry.GetProperty("httpStatus").GetInt32());
        Assert.Contains("Unknown machine.", entry.GetProperty("exceptionMessage").GetString());
    }

    [Fact]
    public async Task REQ_BT_045_Real_network_failure_on_a_closed_port_after_login()
    {
        var line = NewLine(wrap: api => new DeadAfterLogin(api));

        await RunOneAsync(line);

        var entry = OnlyError(await LogsAsync(line));
        Assert.Equal("network-failure", entry.GetProperty("reason").GetString());
        Assert.Equal(nameof(HttpRequestException), entry.GetProperty("exceptionType").GetString());
        Assert.Equal(200, entry.GetProperty("httpStatus").GetInt32());
    }

    [Fact]
    public async Task REQ_BT_045_Real_unknown_when_the_answer_is_a_web_page_not_the_api()
    {
        var line = NewLine(wrap: api => new WebPageAfterLogin(api));

        await RunOneAsync(line);

        var entry = OnlyError(await LogsAsync(line));
        Assert.Equal("unknown", entry.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("exceptionMessage").GetString()));
    }

    [Fact]
    public async Task REQ_BT_045_Started_and_closed_are_info_entries_of_a_real_walk()
    {
        StartDispatcher();
        var line = NewLine();

        await RunOneAsync(line);

        var logs = await LogsAsync(line, "?level=info");
        Assert.Equal(["closed", "started"], logs.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("reason").GetString()));
        var closed = logs.GetProperty("entries")[0];
        Assert.Equal("Closed", closed.GetProperty("step").GetString());
        Assert.True(closed.GetProperty("waitedMs").GetInt64() > 0);
        Assert.Equal(0, (await Client(line, "ada").GetFromJsonAsync<LineStatus>("/line", JsonDefaults.Options))!.Errors);
    }

    [Fact]
    public async Task REQ_BT_045_Rejected_is_an_info_entry_and_never_an_error()
    {
        StartDispatcher();
        var line = NewLine();

        await RunOneAsync(line, 10);

        var logs = await LogsAsync(line, "?reason=rejected");
        Assert.Equal("info", Assert.Single(logs.GetProperty("entries").EnumerateArray()).GetProperty("level").GetString());
        Assert.Equal(0, (await LogsAsync(line, "?level=error")).GetProperty("matched").GetInt32());
    }

    [Fact]
    public async Task REQ_BT_045_Reply_reports_matched_per_reason_dropped_and_evicted_and_status_agrees()
    {
        var line = NewLine(new LineOptions { MachineToken = "wrong" });
        for (var i = 1; i <= 3; i++) await RunOneAsync(line, i);

        var logs = await LogsAsync(line, "?level=error&limit=1");
        var status = (await Client(line, "ada").GetFromJsonAsync<LineStatus>("/line", JsonDefaults.Options))!;

        Assert.Equal(3, logs.GetProperty("matched").GetInt32());
        Assert.Equal(3, logs.GetProperty("byReason").GetProperty("login-failure").GetInt32());
        Assert.Equal(2, logs.GetProperty("droppedByLimit").GetInt32());
        Assert.Equal(0, logs.GetProperty("evicted").GetInt64());
        Assert.Single(logs.GetProperty("entries").EnumerateArray());
        Assert.Equal(3, status.Errors);
        Assert.Equal(3, status.ErrorsByReason["login-failure"]);
    }

    [Fact]
    public async Task REQ_BT_045_Time_filter_works_through_the_query_string()
    {
        var line = NewLine(new LineOptions { MachineToken = "wrong" });
        await RunOneAsync(line);

        var past = await LogsAsync(line, "?to=2000-01-01T00:00:00Z");
        var now = await LogsAsync(line, "?from=2000-01-01T00:00:00Z&to=2999-01-01T00:00:00Z&farm=farm-b&batch=1&level=error&reason=login-failure&limit=5");

        Assert.Equal(0, past.GetProperty("matched").GetInt32());
        Assert.Equal(1, now.GetProperty("matched").GetInt32());
    }

    [Theory]
    [InlineData("?level=warn")]
    [InlineData("?reason=nope")]
    [InlineData("?limit=0")]
    [InlineData("?limit=5001")]
    [InlineData("?limit=abc")]
    [InlineData("?from=yesterday")]
    [InlineData("?from=2026-02-01T00:00:00Z&to=2026-01-01T00:00:00Z")]
    public async Task REQ_BT_045_Bad_filter_is_400(string query)
    {
        var response = await Client(NewLine(), "ada").GetAsync($"/line/logs{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("olivia", HttpStatusCode.Forbidden)]
    [InlineData("ian", HttpStatusCode.Forbidden)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    public async Task REQ_BT_045_Logs_need_ops_admin(string? user, HttpStatusCode expected)
    {
        Assert.Equal(expected, (await Client(NewLine(), user).GetAsync("/line/logs")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_045_Empty_log_is_an_empty_reply_not_an_error()
    {
        var logs = await LogsAsync(NewLine());

        Assert.Equal(0, logs.GetProperty("matched").GetInt32());
        Assert.Equal(0, logs.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task REQ_BT_045_No_token_appears_in_a_real_reply()
    {
        var line = NewLine(wrap: api => new ActsAsViewer(api));
        await RunOneAsync(line);

        var text = await Client(line, "ada").GetStringAsync("/line/logs");

        Assert.DoesNotContain("eyJ", text);
        Assert.DoesNotContain("Bearer", text);
    }

    /// <summary>The credential itself cannot lack a permission, so the refusal is earned with a viewer's token against the real API.</summary>
    private sealed class ActsAsViewer(HttpMessageHandler api) : DelegatingHandler(api)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("auth/machine"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer", new TokenIssuer(Env.PrivateKey, TimeProvider.System).Issue(DemoUsers.Find("viewer")!));
            }
            return base.SendAsync(request, token);
        }
    }

    private sealed class DeadAfterLogin
(HttpMessageHandler api) : DelegatingHandler(api)
    {
        private static readonly HttpMessageInvoker Dead = new(new SocketsHttpHandler());

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("auth/machine")) return base.SendAsync(request, token);
            request.RequestUri = new Uri("http://127.0.0.1:9/");
            return Dead.SendAsync(request, token);
        }
    }

    private sealed class WebPageAfterLogin(HttpMessageHandler api) : DelegatingHandler(api)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            request.RequestUri!.AbsolutePath.EndsWith("auth/machine")
                ? base.SendAsync(request, token)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<!doctype html><html></html>", System.Text.Encoding.UTF8, "text/html")
                });
    }
}
