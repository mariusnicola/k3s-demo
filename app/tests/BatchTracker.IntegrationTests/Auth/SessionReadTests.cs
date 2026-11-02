using System.Net;
using System.Text.Json;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BatchTracker.IntegrationTests;

/// <summary>A page load asks who is signed in here, so a signed-out load shows no failed request and spends no failure, whatever cookie it carries.</summary>
public class SessionReadTests(Stack stack) : FailureTest(stack)
{
    private const string Address = "203.0.113.70";
    private const string Path = "/api/session";

    private static async Task AssertNobodyAsync(HttpResponseMessage reply)
    {
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var body = JsonDocument.Parse(await reply.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Null, body.GetProperty("me").ValueKind);
    }

    private static async Task<HttpResponseMessage> ReadAsync(HttpClient client) => await client.GetAsync(Path);

    private static HttpClient WithCookie<T>(WebApplicationFactory<T> host, string cookie) where T : class
    {
        var client = From(host, Address);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    [Fact]
    public async Task REQ_BT_049_A_signed_out_load_gets_nobody_and_spends_no_failure()
    {
        var host = Api(Short);
        var client = From(host, Address);

        for (var i = 0; i < 12; i++)
        {
            var started = await TimeAsync(() => client.GetAsync(Path));
            Assert.Equal(HttpStatusCode.OK, started.Status);
            Assert.True(started.Took < Step, $"{started.Took}");
        }

        await AssertNobodyAsync(await ReadAsync(client));
        Assert.Equal(0, LedgerOf(host).FailuresOf(Address));
    }

    [Fact]
    public async Task REQ_BT_049_A_stale_cookie_gets_nobody_and_spends_no_failure()
    {
        var host = Api(Short);

        await AssertNobodyAsync(await ReadAsync(WithCookie(host, $"{SessionCookie.Name}=stale")));

        Assert.Equal(0, LedgerOf(host).FailuresOf(Address));
    }

    [Fact]
    public async Task REQ_BT_049_A_bad_bearer_gets_nobody_and_spends_no_failure()
    {
        var host = Api(Short);

        await AssertNobodyAsync(await ReadAsync(From(host, Address, bearer: "guess")));

        Assert.Equal(0, LedgerOf(host).FailuresOf(Address));
    }

    [Fact]
    public async Task REQ_BT_049_A_refused_address_still_gets_an_answer()
    {
        var host = Api(Short with { FirstDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(1) });
        var client = From(host, Address);
        for (var i = 0; i < 9; i++) await client.GetAsync("/api/summary");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/summary")).StatusCode);

        await AssertNobodyAsync(await ReadAsync(client));
    }

    [Fact]
    public async Task REQ_BT_049_A_signed_in_load_gets_the_body_of_me()
    {
        var host = Api(Short);
        var reply = await Login(From(host, "203.0.113.71"), "ada");
        var client = WithCookie(host, reply.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);

        var me = await client.GetStringAsync("/api/me");
        var session = JsonDocument.Parse(await client.GetStringAsync(Path)).RootElement;

        Assert.Equal(me, session.GetProperty("me").GetRawText());
    }
}
