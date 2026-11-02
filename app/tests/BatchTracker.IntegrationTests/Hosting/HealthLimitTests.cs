using System.Net;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

/// <summary>Health and metrics routes have their own bucket: a flood gets a 429, a probe never does.</summary>
public class HealthLimitTests(Stack stack) : FailureTest(stack)
{
    private const int Flood = 100;

    /// <summary>Hosts run with roomy limits, so the real ones go back on here.</summary>
    private static WebApplicationFactory<T> RealRules<T>(WebApplicationFactory<T> host) where T : class =>
        host.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<RateRules>().AddSingleton(new RateRules())));

    /// <summary>The same, for a host that takes its replacements as an action.</summary>
    private static void RealRules(IServiceCollection services) => services.RemoveAll<RateRules>().AddSingleton(new RateRules());

    /// <summary>A refusal carries its wait and a problem body, and the burst of 40 got through first.</summary>
    private static async Task AssertFloodRefusedAsync(HttpClient client, string path)
    {
        var replies = await Task.WhenAll(Enumerable.Range(0, Flood).Select(_ => client.GetAsync(path)));

        var refused = replies.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.True(int.Parse(refused.Headers.GetValues("Retry-After").Single()) > 0);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        Assert.True(replies.Count(r => r.StatusCode != HttpStatusCode.TooManyRequests) >= 40, path);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/api/healthz")]
    [InlineData("/api/ready")]
    [InlineData("/metrics")]
    public async Task REQ_BT_050_Api_refuses_a_flood_of_health_and_metrics(string path)
    {
        await AssertFloodRefusedAsync(RealRules<BatchTracker.Api.ApiMarker>(Stack.Api).CreateClient(), path);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/ready")]
    public async Task REQ_BT_050_Live_refuses_a_flood_of_health(string path)
    {
        await using var live = new LiveHost();

        await AssertFloodRefusedAsync(RealRules(live).CreateClient(), path);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/ready")]
    public async Task REQ_BT_050_Line_refuses_a_flood_of_health(string path)
    {
        await using var line = new LineHost(Stack.Api);

        await AssertFloodRefusedAsync(RealRules(line).CreateClient(), path);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/metrics")]
    public async Task REQ_BT_050_Worker_refuses_a_flood_of_health_and_metrics(string path)
    {
        await using var worker = new WorkerHost(RealRules);

        await AssertFloodRefusedAsync(worker.CreateClient(), path);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/metrics")]
    [InlineData("/status")]
    public async Task REQ_BT_050_Dispatcher_refuses_a_flood_on_every_route(string path)
    {
        await using var dispatcher = new DispatcherHost(RealRules);

        await AssertFloodRefusedAsync(dispatcher.CreateClient(), path);
    }

    /// <summary>The real machine limit needs over 1000 calls to reach, so a small one proves the route uses it.</summary>
    private static void TightMachine(IServiceCollection services) =>
        services.RemoveAll<RateRules>().AddSingleton(new RateRules { Machine = new RateRule(5, 10) });

    [Theory]
    [InlineData("/events")]
    [InlineData("/nightly/run")]
    public async Task REQ_BT_050_Worker_refuses_a_flood_on_its_internal_routes(string path)
    {
        await using var worker = new WorkerHost(TightMachine);
        var client = worker.CreateClient();

        var replies = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => client.PostAsync(path, null)));

        var refused = replies.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.True(int.Parse(refused.Headers.GetValues("Retry-After").Single()) > 0);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Sends one internal call, because a 429 here abandons the event and three of those dead-letter it.</summary>
    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(HttpWorkerClient.TokenHeader, token);
        return client.SendAsync(request);
    }

    /// <summary>12 is past the burst of 10, and each /nightly/run is a real compliance scan, so no more than that.</summary>
    [Theory]
    [InlineData("/events", 60)]
    [InlineData("/nightly/run", 12)]
    public async Task REQ_BT_050_A_valid_internal_token_is_never_refused(string path, int calls)
    {
        await using var worker = new WorkerHost(TightMachine);
        var client = worker.CreateClient();

        var replies = await Task.WhenAll(Enumerable.Range(0, calls).Select(_ => PostAsync(client, path, Env.InternalToken)));

        Assert.All(replies, r => Assert.NotEqual(HttpStatusCode.TooManyRequests, r.StatusCode));
    }

    [Theory]
    [InlineData("/reports")]
    [InlineData("/hops")]
    [InlineData("/changes")]
    [InlineData("/logs")]
    public async Task REQ_BT_050_Live_reports_refuse_a_flood_without_the_token(string path)
    {
        await using var live = new LiveHost();
        var client = live.WithWebHostBuilder(b => b.ConfigureTestServices(TightMachine)).CreateClient();

        var replies = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => client.PostAsync(path, null)));

        var refused = replies.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/reports")]
    [InlineData("/hops")]
    [InlineData("/changes")]
    [InlineData("/logs")]
    public async Task REQ_BT_050_Live_reports_with_the_token_are_never_refused(string path)
    {
        await using var live = new LiveHost();
        var client = live.WithWebHostBuilder(b => b.ConfigureTestServices(TightMachine)).CreateClient();

        var replies = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => PostAsync(client, path, Env.InternalToken)));

        Assert.All(replies, r => Assert.NotEqual(HttpStatusCode.TooManyRequests, r.StatusCode));
    }

    [Fact]
    public async Task REQ_BT_050_A_wrong_internal_token_stays_limited()
    {
        await using var worker = new WorkerHost(TightMachine);
        var client = worker.CreateClient();

        var replies = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => PostAsync(client, "/events", "wrong")));

        Assert.Contains(replies, r => r.StatusCode == HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// The chart's busiest pod takes 0.4 probes a second from its kubelet plus a scrape every 15 s, all from one address.
    /// This runs at 15 a second, past the burst of 40, so a real rate cannot empty the bucket.
    /// </summary>
    [Fact]
    public async Task REQ_BT_050_A_probe_at_its_real_rate_never_gets_a_429()
    {
        var client = RealRules<BatchTracker.Api.ApiMarker>(Stack.Api).CreateClient();
        string[] paths = ["/api/healthz", "/api/ready", "/metrics"];

        for (var i = 0; i < 45; i++)
        {
            var reply = await client.GetAsync(paths[i % paths.Length]);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, reply.StatusCode);
            await Task.Delay(TimeSpan.FromMilliseconds(66));
        }
    }

    [Fact]
    public async Task REQ_BT_050_An_anonymous_flood_leaves_health_alone()
    {
        var client = From(RealRules<BatchTracker.Api.ApiMarker>(Stack.Api), "203.0.113.70");
        await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => client.GetAsync("/api/me")));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/healthz")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_049_A_refused_health_call_spends_no_failure()
    {
        var host = Delayed(RealRules<BatchTracker.Api.ApiMarker>(Stack.Api), Short);
        var client = From(host, "203.0.113.71");

        var (status, took) = await TimeAsync(async () =>
        {
            var replies = await Task.WhenAll(Enumerable.Range(0, Flood).Select(_ => client.GetAsync("/api/healthz")));
            return replies.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.True(took < Step, $"{took}");
        Assert.Equal(0, LedgerOf(host).FailuresOf("203.0.113.71"));
    }
}
