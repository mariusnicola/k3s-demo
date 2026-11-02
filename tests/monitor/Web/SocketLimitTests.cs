using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using Xunit;

/// <summary>F12 and REQ-BT-050: a socket is refused over its cap with 429, Retry-After and a problem body.</summary>
public class SocketLimitTests
{
    private const string Origin = "http://ingress.test";
    private const string TestPeer = "127.0.0.0/8";
    private static readonly string PublicKey = Convert.ToBase64String(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo());

    private sealed record Stand(WebApplication Host, string Url, string Http);

    private static async Task<Stand> Start(SocketLimits limits)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var host = builder.Build();
        Routes.MapHealth(host, () => null, 0, ProbeLimits.Standard(limits));
        var hub = new SocketHub(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        MonitorSocket.Use(host, Origin, new SessionReader(PublicKey), limits, hub, () => null, new ChangeFeed(hub), new Operations(hub));
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Stand(host, address.Replace("http://", "ws://") + "/ws", address);
    }

    private static async Task<ClientWebSocket> Open(Stand stand, string client)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", Origin);
        socket.Options.SetRequestHeader("X-Forwarded-For", client);
        socket.Options.CollectHttpResponseDetails = true;
        await socket.ConnectAsync(new Uri(stand.Url), new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        return socket;
    }

    private static async Task<(int Status, string? RetryAfter, string? Type)> Refused(Stand stand, string client)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, stand.Http + "/ws");
        foreach (var (name, value) in new[] { ("Connection", "Upgrade"), ("Upgrade", "websocket"), ("Sec-WebSocket-Version", "13"), ("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ=="), ("Origin", Origin), ("X-Forwarded-For", client) })
            request.Headers.TryAddWithoutValidation(name, value);
        using var answer = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return ((int)answer.StatusCode, answer.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(), answer.Content.Headers.ContentType?.MediaType);
    }

    private static SocketLimits Wide(int perClient = 100, int total = 100) => new(perClient, total, burst: 1000, perSecond: 1000, TestPeer);

    [Fact]
    public async Task F12_one_client_over_its_cap_gets_429_with_retry_after_and_a_problem_body()
    {
        var stand = await Start(Wide(perClient: 2));
        await using var _ = stand.Host;
        using var first = await Open(stand, "198.18.2.1");
        using var second = await Open(stand, "198.18.2.1");

        var (status, retry, type) = await Refused(stand, "198.18.2.1");

        Assert.Equal(429, status);
        Assert.Equal("30", retry);
        Assert.Equal("application/problem+json", type);
    }

    [Fact]
    public async Task F12_another_client_still_gets_a_socket()
    {
        var stand = await Start(Wide(perClient: 1));
        await using var _ = stand.Host;
        using var first = await Open(stand, "198.18.2.1");

        using var second = await Open(stand, "198.18.2.2");

        Assert.Equal(WebSocketState.Open, second.State);
    }

    [Fact]
    public async Task F12_a_written_address_cannot_pass_the_total_cap()
    {
        var stand = await Start(Wide(total: 2));
        await using var _ = stand.Host;
        using var first = await Open(stand, "198.18.2.1");
        using var second = await Open(stand, "198.18.2.2");

        Assert.Equal(429, (await Refused(stand, "198.18.2.3")).Status);
    }

    [Fact]
    public async Task F12_a_closed_socket_gives_its_place_back()
    {
        var stand = await Start(Wide(perClient: 1));
        await using var _ = stand.Host;
        var first = await Open(stand, "198.18.2.1");
        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        first.Dispose();

        using var again = await OpenSoon(stand, "198.18.2.1", DateTime.UtcNow.AddSeconds(5));

        Assert.Equal(WebSocketState.Open, again.State);
    }

    private static async Task<ClientWebSocket> OpenSoon(Stand stand, string client, DateTime deadline)
    {
        while (true)
        {
            try
            {
                return await Open(stand, client);
            }
            catch (WebSocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
    }

    [Fact]
    public async Task REQ_BT_050_upgrades_faster_than_the_rate_get_429()
    {
        var stand = await Start(new SocketLimits(perClient: 100, total: 100, burst: 2, perSecond: 1, TestPeer));
        await using var _ = stand.Host;
        (await Open(stand, "198.18.2.1")).Dispose();
        (await Open(stand, "198.18.2.1")).Dispose();

        var (status, retry, _) = await Refused(stand, "198.18.2.1");

        Assert.Equal(429, status);
        Assert.NotNull(retry);
    }

    [Fact]
    public async Task TM_2413_a_full_socket_cap_leaves_the_probe_route_alone()
    {
        var stand = await Start(new SocketLimits(perClient: 0, total: 0, burst: 1, perSecond: 1, TestPeer));
        await using var _ = stand.Host;

        Assert.Equal(429, (await Refused(stand, "198.18.2.1")).Status);
        using var http = new HttpClient();
        Assert.Equal(200, (int)(await http.GetAsync(stand.Http + "/alive")).StatusCode);
    }

    [Fact]
    public async Task T_011_with_no_trusted_proxy_a_written_address_changes_nothing()
    {
        var stand = await Start(new SocketLimits(perClient: 1, total: 100, burst: 1000, perSecond: 1000, trusted: null));
        await using var _ = stand.Host;
        using var first = await Open(stand, "198.18.2.1");

        Assert.Equal(429, (await Refused(stand, "198.18.2.2")).Status);
    }

    [Fact]
    public async Task T_011_a_peer_outside_the_list_is_not_believed()
    {
        var stand = await Start(new SocketLimits(perClient: 1, total: 100, burst: 1000, perSecond: 1000, trusted: "192.0.2.0/24, 198.51.100.7/32"));
        await using var _ = stand.Host;
        using var first = await Open(stand, "198.18.2.1");

        Assert.Equal(429, (await Refused(stand, "198.18.2.2")).Status);
    }

    [Fact]
    public void T_011_a_bad_entry_stops_the_start()
    {
        Assert.Throws<FormatException>(() => SocketLimits.Standard("198.18.0.0/99"));
    }

    [Fact]
    public async Task F12_the_key_is_the_last_forwarded_entry_so_a_written_one_changes_nothing()
    {
        var stand = await Start(Wide(perClient: 1));
        await using var _ = stand.Host;
        using var first = await Open(stand, "198.18.2.1");

        Assert.Equal(429, (await Refused(stand, "1.2.3.4, 198.18.2.1")).Status);
    }
}
