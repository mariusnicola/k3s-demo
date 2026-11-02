using System.Collections.Concurrent;
using System.Net.Http.Json;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace BatchTracker.IntegrationTests;

public class RequestLogTests(Stack stack) : StackTest(stack)
{
    private const string Marker = "AUDITMARKER";

    private sealed class Capture : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() { }

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue($"{formatter(state, exception)} {exception}");
    }

    private static HttpClient Client<T>(WebApplicationFactory<T> host, Capture capture) where T : class =>
        host.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(capture)))
            .CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private async Task AssertAbsentAsync<T>(WebApplicationFactory<T> host, string path) where T : class
    {
        var capture = new Capture();
        var client = Client(host, capture);

        await client.GetAsync($"{path}?token={Marker}");

        Assert.DoesNotContain(capture.Lines, line => line.Contains(Marker));
    }

    [Fact]
    public async Task REQ_BT_047_Token_sent_in_the_url_stays_out_of_the_api_log()
    {
        await AssertAbsentAsync(Stack.Api, "/api/me");
    }

    [Fact]
    public async Task REQ_BT_047_Token_sent_in_the_url_stays_out_of_the_live_log()
    {
        await using var live = new LiveHost();

        await AssertAbsentAsync(live, "/ws/hops");
    }

    [Fact]
    public async Task REQ_BT_047_Token_sent_in_the_url_stays_out_of_the_line_log()
    {
        await using var line = new LineHost(Stack.Api);

        await AssertAbsentAsync(line, "/line");
    }

    [Fact]
    public async Task REQ_BT_047_Token_sent_in_the_url_stays_out_of_the_worker_log()
    {
        await AssertAbsentAsync(Stack.Worker, "/ready");
    }

    [Fact]
    public async Task REQ_BT_047_Sign_in_refusal_and_failed_auth_log_no_token_or_cookie()
    {
        var capture = new Capture();
        var client = Client(Stack.Api, capture);

        var login = await client.PostAsJsonAsync("/api/auth/dev-login", new { user = "ada" });
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var value = cookie.Split('=')[1];
        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        me.Headers.Add("Cookie", cookie);
        await client.SendAsync(me);
        using var tampered = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        tampered.Headers.Add("Cookie", cookie + "x");
        await client.SendAsync(tampered);
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/batches");
        foreign.Headers.Add("Cookie", cookie);
        foreign.Headers.Add("Origin", "http://evil.example");
        await client.SendAsync(foreign);

        Assert.NotEmpty(capture.Lines);
        Assert.DoesNotContain(capture.Lines, line => line.Contains(value) || line.Contains("eyJ") || line.Contains(SessionCookie.Name));
    }
}
