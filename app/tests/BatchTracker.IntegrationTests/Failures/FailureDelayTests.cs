using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

public class FailureDelayTests(Stack stack) : FailureDelayTest(stack)
{
    [Fact]
    public async Task REQ_BT_049_Each_401_past_the_free_ones_is_slower_until_the_cap()
    {
        var client = From(Api());
        var took = new List<TimeSpan>();

        for (var i = 0; i < 8; i++)
        {
            var (status, time) = await TimeAsync(() => client.GetAsync("/api/summary"));
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            took.Add(time);
        }

        Assert.True(Held(took[2], 1), $"{took[2]}");
        Assert.True(Held(took[3], 2), $"{took[3]}");
        Assert.True(Held(took[4], 4), $"{took[4]}");
        Assert.True(Held(took[7], 4) && took[7] < 16 * Step, $"{took[7]}");
    }

    [Fact]
    public async Task REQ_BT_049_Failed_dev_login_counts_and_keeps_its_problem_body()
    {
        var host = Api();
        var client = From(host);
        await Login(client, "nobody");
        await Login(client, "nobody");
        var clock = Stopwatch.StartNew();

        var reply = await Login(client, "nobody");

        Assert.True(clock.Elapsed >= Step - Timing.Tick, $"{clock.Elapsed}");
        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
        Assert.Contains("Unknown user", await reply.Content.ReadAsStringAsync());
        Assert.Equal(3, LedgerOf(host).FailuresOf("203.0.113.20"));
    }

    [Fact]
    public async Task REQ_BT_049_Wrong_machine_token_counts_and_the_right_one_is_not_delayed()
    {
        var host = Api();
        var client = From(host);
        for (var i = 0; i < 3; i++)
        {
            var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/auth/machine");
            wrong.Headers.Add(MachineCredential.Header, "wrong");
            await client.SendAsync(wrong);
        }
        Assert.Equal(3, LedgerOf(host).FailuresOf("203.0.113.20"));
        var right = new HttpRequestMessage(HttpMethod.Post, "/api/auth/machine");
        right.Headers.Add(MachineCredential.Header, Env.LineMachineToken);

        var (status, took) = await TimeAsync(() => client.SendAsync(right));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(took < Step, $"{took}");
    }

    [Fact]
    public async Task REQ_BT_049_Over_ten_at_once_get_429_with_retry_after_and_no_route_work()
    {
        var host = Api(Short with { RefuseFrom = int.MaxValue, FirstDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(1) });
        var client = From(host);
        await SpendFreeFailuresAsync(client);

        var replies = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Login(client, "nobody")));

        var refused = replies.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        Assert.Equal(20, refused.Count);
        Assert.Equal(10, replies.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
        Assert.All(refused, r => Assert.True(int.Parse(r.Headers.GetValues("Retry-After").Single()) > 0));
        Assert.Equal("application/problem+json", refused[0].Content.Headers.ContentType?.MediaType);
        Assert.Equal(12, LedgerOf(host).FailuresOf("203.0.113.20"));
    }

    [Fact]
    public async Task REQ_BT_049_Empty_challenges_hold_their_place_for_the_whole_delay()
    {
        var host = Api(Short with { RefuseFrom = int.MaxValue, FirstDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(1) });
        var client = From(host);
        await SpendFreeFailuresAsync(client);

        var replies = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => client.GetAsync("/api/summary")));

        Assert.Equal(20, replies.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Equal(10, replies.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task REQ_BT_049_Signed_in_caller_sharing_an_address_with_a_full_client_is_not_refused()
    {
        // Off, or the refusal limit turns the last of the ten away before they hold a seat.
        var host = Api(Short with { RefuseFrom = int.MaxValue, FirstDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(1) });
        var client = From(host);
        await SpendFreeFailuresAsync(client);
        var held = Enumerable.Range(0, 10).Select(_ => client.GetAsync("/api/summary")).ToList();
        await UntilWaitingAsync(host, 10);

        var (status, took) = await TimeAsync(() => From(host, bearer: Sockets.TokenFor("ada")).GetAsync("/api/me"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(took < TimeSpan.FromMilliseconds(500), $"{took}");
        Assert.Equal(10, LedgerOf(host).WaitingOf("203.0.113.20"));
        await Task.WhenAll(held);
    }

    /// <summary>Under the 1 s hold, so a request that never joins fails here and not by an earlier one leaving.</summary>
    private static async Task UntilWaitingAsync(WebApplicationFactory<BatchTracker.Api.ApiMarker> host, int count)
    {
        var clock = Stopwatch.StartNew();
        while (LedgerOf(host).WaitingOf("203.0.113.20") < count)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(800), $"{LedgerOf(host).WaitingOf("203.0.113.20")} of {count} held after {clock.Elapsed}");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task REQ_BT_049_Bad_and_expired_tokens_count_like_no_token()
    {
        var host = Api();
        var bad = From(host, bearer: "not-a-token");
        var expired = From(host, bearer: Sockets.TokenFor("ada", new FixedTime(DateTimeOffset.UtcNow.AddDays(-2))));

        await bad.GetAsync("/api/summary");
        await expired.GetAsync("/api/summary");
        var (status, took) = await TimeAsync(() => bad.GetAsync("/api/summary"));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.True(took >= Step - Timing.Tick, $"{took}");
    }

    [Fact]
    public async Task REQ_BT_049_After_the_quiet_period_the_next_failure_answers_at_once()
    {
        var host = Api(Short with { Quiet = TimeSpan.FromMilliseconds(200) });
        var client = From(host);
        await SpendFreeFailuresAsync(client);
        await Task.Delay(300);

        var (_, took) = await TimeAsync(() => client.GetAsync("/api/summary"));

        Assert.True(took < Step, $"{took}");
    }

    [Fact]
    public async Task REQ_BT_049_A_spoofed_first_entry_gets_no_fresh_count()
    {
        var host = Api();
        for (var i = 0; i < 3; i++) await From(host, $"198.51.100.{i}, 203.0.113.21").GetAsync("/api/summary");

        Assert.Equal(3, LedgerOf(host).FailuresOf("203.0.113.21"));
    }

    [Fact]
    public async Task REQ_BT_049_Valid_token_from_a_client_under_delay_answers_at_once_and_counts_nothing()
    {
        var host = Api();
        await SpendFreeFailuresAsync(From(host));
        var signedIn = From(host, bearer: Sockets.TokenFor("ada"));

        var (status, took) = await TimeAsync(() => signedIn.GetAsync("/api/me"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(took < Step, $"{took}");
        Assert.Equal(2, LedgerOf(host).FailuresOf("203.0.113.20"));
    }

    [Fact]
    public async Task REQ_BT_049_Valid_sign_in_answers_at_once_and_clears_no_count()
    {
        var host = Api();
        var client = From(host);
        var other = From(host, "203.0.113.22");
        await SpendFreeFailuresAsync(client);
        await SpendFreeFailuresAsync(other);

        var (status, took) = await TimeAsync(() => Login(client, "ada"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(took < Step, $"{took}");
        Assert.Equal(2, LedgerOf(host).FailuresOf("203.0.113.20"));
        Assert.Equal(2, LedgerOf(host).FailuresOf("203.0.113.22"));
    }

    [Fact]
    public async Task REQ_BT_049_Known_and_unknown_names_get_delays_by_count_alone()
    {
        var host = Api();
        var client = From(host);
        await Login(client, "nobody");
        await Login(client, "someone");

        var (first, firstTook) = await TimeAsync(() => Login(client, "nobody"));
        var (second, secondTook) = await TimeAsync(() => Login(client, "someone"));

        Assert.Equal(first, second);
        Assert.True(firstTook >= Step - Timing.Tick && secondTook >= 2 * Step - Timing.Tick, $"{firstTook} {secondTook}");
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/api/healthz")]
    [InlineData("/api/ready")]
    [InlineData("/api/ready/deep")]
    [InlineData("/metrics")]
    public async Task REQ_BT_049_Open_routes_answer_at_once_and_count_nothing(string path)
    {
        var host = Api();
        var limited = From(host);
        await SpendFreeFailuresAsync(limited);

        for (var i = 0; i < 12; i++)
        {
            var (status, took) = await TimeAsync(() => limited.GetAsync(path));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, status);
            Assert.True(took < Step * 10, $"{took}");
        }
        Assert.Equal(2, LedgerOf(host).FailuresOf("203.0.113.20"));
    }

    [Fact]
    public async Task REQ_BT_049_Health_from_a_client_holding_every_place_is_not_refused()
    {
        var host = Api(Short with { FirstDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(1) });
        var client = From(host);
        await SpendFreeFailuresAsync(client);
        var held = Enumerable.Range(0, 10).Select(_ => client.GetAsync("/api/summary")).ToList();
        await Task.Delay(100);

        var reply = await client.GetAsync("/api/healthz");

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        await Task.WhenAll(held);
    }
}
