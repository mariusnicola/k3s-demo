using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Live;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BatchTracker.IntegrationTests;

/// <summary>Who may open /ws/hops and how the socket ends.</summary>
public class LiveSocketAccessTests(Stack stack) : LiveTest(stack)
{
    [Fact]
    public async Task REQ_BT_047_Socket_with_no_session_is_401()
    {
        Assert.Contains("401", await Sockets.RefusalAsync(Server, Path, null));
    }

    [Theory]
    [InlineData("?token=not.a.token")]
    [InlineData("?token=")]
    public async Task REQ_BT_047_Socket_with_a_bad_query_token_is_401(string query)
    {
        Assert.Contains("401", await Sockets.RefusalAsync(Server, Path + query, null));
    }

    [Fact]
    public async Task REQ_BT_047_Valid_token_in_the_url_never_opens_a_socket()
    {
        var path = $"{Path}?token={Sockets.TokenFor("viewer")}";

        Assert.Contains("401", await Sockets.RefusalAsync(Server, path, null));
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("null")]
    [InlineData("http://batch-tracker.test:8080")]
    public async Task REQ_BT_047_Cookie_from_another_origin_is_403_and_no_socket(string origin)
    {
        Assert.Contains("403", await Sockets.RefusalAsync(Server, Path, Sockets.CookieFor("viewer"), origin));
    }

    [Fact]
    public async Task REQ_BT_047_Cookie_with_no_origin_is_403()
    {
        Assert.Contains("403", await Sockets.RefusalAsync(Server, Path, Sockets.CookieFor("viewer"), null));
    }

    [Fact]
    public async Task REQ_BT_047_Bearer_from_another_origin_is_403()
    {
        Assert.Contains("403", await Sockets.RefusalAsync(Server, Path, null, "http://evil.example", Sockets.TokenFor("viewer")));
    }

    [Fact]
    public async Task REQ_BT_047_Bearer_with_no_origin_opens_a_socket()
    {
        using var socket = await Sockets.OpenAsync(Server, Path, null, null, Sockets.TokenFor("viewer"));

        Assert.Equal("snapshot", (await Sockets.ReceiveAsync(socket)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task REQ_BT_047_Socket_closes_4401_when_its_token_expires()
    {
        using var socket = await Sockets.OpenAsync(Server, Path, Sockets.CookieFor("viewer", new ExpiresIn(TimeSpan.FromSeconds(2))));
        await Sockets.ReceiveAsync(socket);

        var (status, reason) = await Sockets.WaitForCloseAsync(socket, Limit);

        Assert.Equal(4401, (int?)status);
        Assert.Equal("unauthorized", reason);
    }

    [Fact]
    public async Task REQ_BT_047_Socket_stays_open_before_its_token_expires()
    {
        using var socket = await Sockets.OpenAsync(Server, Path, Sockets.CookieFor("viewer", new ExpiresIn(TimeSpan.FromSeconds(6))));
        await Sockets.ReceiveAsync(socket);

        await PostAsync(NewHop("farm-a", "still-open"));

        Assert.Equal("hops", (await Sockets.ReceiveAsync(socket, TimeSpan.FromSeconds(2))).GetProperty("type").GetString());
    }

    [Fact]
    public async Task REQ_BT_050_Sixth_socket_of_a_user_is_429_and_another_user_still_connects()
    {
        var open = new List<WebSocket>();
        for (var i = 0; i < SocketRules.MaxPerUser; i++) open.Add(await OpenAsync("viewer"));

        Assert.Contains("429", await Sockets.RefusalAsync(Server, Path, Sockets.CookieFor("viewer")));
        open.Add(await OpenAsync("olivia"));
        foreach (var socket in open) socket.Dispose();
    }

    [Fact]
    public async Task REQ_BT_050_Closing_a_socket_frees_its_place()
    {
        var open = new List<WebSocket>();
        for (var i = 0; i < SocketRules.MaxPerUser; i++) open.Add(await OpenAsync("viewer"));

        await open[0].CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        using var limit = new CancellationTokenSource(Limit);
        WebSocket? next = null;
        while (next is null)
        {
            limit.Token.ThrowIfCancellationRequested();
            try
            {
                next = await OpenAsync("viewer");
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(20);
            }
        }
        next.Dispose();
        foreach (var socket in open.Skip(1)) socket.Dispose();
    }

    [Fact]
    public async Task REQ_BT_028_Closed_socket_leaves_the_hub()
    {
        var socket = await OpenAsync("ada");
        var hub = live.Services.GetRequiredService<HopHub>();

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        using var limit = new CancellationTokenSource(Limit);
        while (hub.ClientCount > 0)
        {
            limit.Token.ThrowIfCancellationRequested();
            await Task.Delay(10);
        }
    }

    [Fact]
    public void REQ_BT_028_Protocol_ping_every_30_seconds()
    {
        var options = live.Services.GetRequiredService<IOptions<WebSocketOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(30), options.KeepAliveInterval);
    }

    [Fact]
    public async Task REQ_BT_014_Live_healthz_is_anonymous()
    {
        Assert.Equal(HttpStatusCode.OK, (await live.CreateClient().GetAsync("/healthz")).StatusCode);
    }
}
