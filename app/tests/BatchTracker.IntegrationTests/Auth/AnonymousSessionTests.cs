using System.Net;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

/// <summary>A page asking whether anyone is signed in guesses nothing, so a reload costs no failure. A credential on it still counts.</summary>
public class AnonymousSessionTests(Stack stack) : FailureTest(stack)
{
    private const string Address = "203.0.113.60";

    [Fact]
    public async Task REQ_BT_049_Asking_who_is_signed_in_with_no_credential_spends_no_failure()
    {
        var host = Api(Short);
        var client = From(host, Address);

        for (var i = 0; i < 12; i++)
        {
            var (status, took) = await TimeAsync(() => client.GetAsync("/api/me"));
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.True(took < Step, $"{took}");
        }

        Assert.Equal(0, LedgerOf(host).FailuresOf(Address));
    }

    [Fact]
    public async Task REQ_BT_049_A_refused_client_can_still_ask()
    {
        var quick = Short with { FirstDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(1) };
        var host = Api(quick);
        var client = From(host, Address);
        for (var i = 0; i < 9; i++) await client.GetAsync("/api/summary");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_049_A_bad_bearer_on_me_still_counts()
    {
        var host = Api(Short);

        await From(host, Address, bearer: "guess").GetAsync("/api/me");

        Assert.Equal(1, LedgerOf(host).FailuresOf(Address));
    }

    [Fact]
    public async Task REQ_BT_049_A_bad_cookie_on_me_still_counts()
    {
        var host = Api(Short);
        var client = From(host, Address);
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionCookie.Name}=guess");

        await client.GetAsync("/api/me");

        Assert.Equal(1, LedgerOf(host).FailuresOf(Address));
    }

    /// <summary>The cookie a real sign-in sets, taken from another address so the sign-in spends nothing on the one under test.</summary>
    private static async Task<HttpClient> SignedInAsync<T>(WebApplicationFactory<T> host) where T : class
    {
        var reply = await Login(From(host, "203.0.113.61"), "ada");
        var client = From(host, Address);
        client.DefaultRequestHeaders.Add("Cookie", reply.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        return client;
    }

    [Fact]
    public async Task REQ_BT_049_A_valid_cookie_on_a_refused_address_reads_me()
    {
        var host = Api(Short with { FirstDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(1) });
        var anonymous = From(host, Address);
        for (var i = 0; i < 9; i++) await anonymous.GetAsync("/api/summary");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anonymous.GetAsync("/api/summary")).StatusCode);

        var reply = await (await SignedInAsync(host)).GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Contains("ada", await reply.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task REQ_BT_050_A_valid_cookie_on_an_address_over_its_rate_reads_me()
    {
        var limited = Stack.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<RateRules>().AddSingleton(new RateRules())));
        var host = Delayed(limited, Short);
        var anonymous = From(host, Address);
        var burst = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => anonymous.GetAsync("/api/me")));
        Assert.Contains(burst, r => r.StatusCode == HttpStatusCode.TooManyRequests);

        var reply = await (await SignedInAsync(host)).GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
    }
}
