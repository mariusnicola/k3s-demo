using System.Net;
using Microsoft.AspNetCore.Builder;
using Xunit;

/// <summary>REQ-BT-050: /alive and /health are limited per client, far above what any probe sends.</summary>
public class ProbeLimitTests
{
    private const string TestPeer = "127.0.0.0/8";
    private static readonly Report Healthy = new(DateTimeOffset.UtcNow, true, [], [], [], [], []);

    private sealed record Stand(WebApplication Host, string Url);

    private static async Task<Stand> Start(int burst, int perSecond)
    {
        var host = TestHost.Create();
        var sockets = SocketLimits.Standard(TestPeer);
        Routes.MapHealth(host, () => Healthy, Routes.ReportPort, new ProbeLimits(sockets.Client, burst, perSecond));
        await host.StartAsync();
        return new Stand(host, TestHost.Url(host));
    }

    private static async Task<HttpResponseMessage> Get(HttpClient http, string url, string client = "198.18.2.1")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Forwarded-For", client);
        return await http.SendAsync(request);
    }

    [Theory]
    [InlineData("/alive")]
    [InlineData("/health")]
    public async Task REQ_BT_050_over_the_rate_a_probe_route_answers_429_with_retry_after_and_a_problem_body(string path)
    {
        var stand = await Start(burst: 1, perSecond: 1);
        await using var _ = stand.Host;
        using var http = new HttpClient();
        (await Get(http, stand.Url + path)).Dispose();

        using var answer = await Get(http, stand.Url + path);

        Assert.Equal(HttpStatusCode.TooManyRequests, answer.StatusCode);
        Assert.NotNull(answer.Headers.RetryAfter?.Delta);
        Assert.True(answer.Headers.RetryAfter!.Delta!.Value.TotalSeconds >= 1);
        Assert.Equal("application/problem+json", answer.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TM_2413_a_flood_from_one_client_leaves_another_probe_alone()
    {
        var stand = await Start(burst: 1, perSecond: 1);
        await using var _ = stand.Host;
        using var http = new HttpClient();
        (await Get(http, stand.Url + "/alive", "198.18.2.1")).Dispose();
        (await Get(http, stand.Url + "/alive", "198.18.2.1")).Dispose();

        using var other = await Get(http, stand.Url + "/alive", "198.18.2.2");

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_050_setup_polling_ten_times_faster_than_it_does_is_never_refused()
    {
        var stand = await StartStandard();
        await using var _ = stand.Host;
        using var http = new HttpClient();

        for (var i = 0; i < 15; i++)
        {
            using var answer = await Get(http, stand.Url + "/alive");
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task REQ_BT_050_a_smoke_run_back_to_back_is_never_refused()
    {
        var stand = await StartStandard();
        await using var _ = stand.Host;
        using var http = new HttpClient();

        for (var i = 0; i < 30; i++)
        {
            using var answer = await Get(http, stand.Url + (i % 2 == 0 ? "/health" : "/alive"));
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        }
    }

    private static async Task<Stand> StartStandard()
    {
        var host = TestHost.Create();
        Routes.MapHealth(host, () => Healthy, Routes.ReportPort, ProbeLimits.Standard(SocketLimits.Standard(TestPeer)));
        await host.StartAsync();
        return new Stand(host, TestHost.Url(host));
    }
}
