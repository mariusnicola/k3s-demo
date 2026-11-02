using System.Net;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BatchTracker.IntegrationTests;

/// <summary>Each real client has its own failure count, whatever header it sends.</summary>
public class RealClientTests(Stack stack) : FailureTest(stack)
{
    private const string PeerHeader = "X-Test-Peer";
    private const string Proxy = "198.18.0.7";
    private const string HostA = "198.51.100.50";
    private const string HostB = "198.51.100.51";

    /// <summary>A test server has no connection, so this stands in for the address a socket would show.</summary>
    private sealed class Peer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, go) =>
            {
                if (context.Request.Headers.TryGetValue(PeerHeader, out var peer)) context.Connection.RemoteIpAddress = IPAddress.Parse(peer!);
                return go(context);
            });
            next(app);
        };
    }

    private WebApplicationFactory<BatchTracker.Api.ApiMarker> Host() => Delayed(Stack.Api.WithWebHostBuilder(b =>
    {
        b.UseSetting("TRUSTED_PROXIES", "198.18.0.0/16");
        b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter, Peer>());
    }), Short);

    /// <summary>A client on this peer, sending this forwarded header.</summary>
    private static HttpClient Client(WebApplicationFactory<BatchTracker.Api.ApiMarker> host, string peer, string? forwarded = null)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add(PeerHeader, peer);
        if (forwarded is not null) client.DefaultRequestHeaders.Add("X-Forwarded-For", forwarded);
        return client;
    }

    [Fact]
    public async Task B_010_A_host_writing_its_own_header_stays_one_client()
    {
        var host = Host();

        for (var i = 0; i < 5; i++) await Client(host, HostA, $"203.0.113.{i}").GetAsync("/api/summary");

        Assert.Equal(5, LedgerOf(host).FailuresOf(HostA));
        Assert.Equal(0, LedgerOf(host).FailuresOf("203.0.113.0"));
    }

    [Fact]
    public async Task B_010_Two_hosts_get_separate_counts()
    {
        var host = Host();

        for (var i = 0; i < 3; i++) await Client(host, HostA).GetAsync("/api/summary");
        await Client(host, HostB).GetAsync("/api/summary");

        Assert.Equal(3, LedgerOf(host).FailuresOf(HostA));
        Assert.Equal(1, LedgerOf(host).FailuresOf(HostB));
    }

    [Fact]
    public async Task B_010_Two_clients_behind_the_ingress_get_separate_counts()
    {
        var host = Host();

        for (var i = 0; i < 3; i++) await Client(host, Proxy, HostA).GetAsync("/api/summary");
        await Client(host, Proxy, HostB).GetAsync("/api/summary");

        Assert.Equal(3, LedgerOf(host).FailuresOf(HostA));
        Assert.Equal(1, LedgerOf(host).FailuresOf(HostB));
        Assert.Equal(0, LedgerOf(host).FailuresOf(Proxy));
    }

    [Fact]
    public async Task B_010_One_hosts_sign_in_leaves_the_others_delay()
    {
        var host = Host();
        for (var i = 0; i < 3; i++) await Client(host, HostA).GetAsync("/api/summary");

        var signedIn = await Login(Client(host, HostB), "ada");

        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Equal(3, LedgerOf(host).FailuresOf(HostA));
    }

    [Fact]
    public void B_010_A_bad_proxy_list_stops_the_start()
    {
        using var bad = Stack.Api.WithWebHostBuilder(b => b.UseSetting("TRUSTED_PROXIES", "198.18.0.0/99"));

        Assert.ThrowsAny<FormatException>(() => bad.CreateClient());
    }
}
