using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BatchTracker.IntegrationTests;

public class SocketSessionTests
{
    /// <summary>A host with one socket route that hands its client to the test and drops it on stop.</summary>
    private static async Task<WebApplication> HostAsync(TaskCompletionSource<SocketClient> opened, bool realListener = false)
    {
        var builder = WebApplication.CreateBuilder();
        if (realListener) builder.WebHost.UseUrls("http://127.0.0.1:0");
        else builder.WebHost.UseTestServer();
        builder.Services.AddUserAuth(builder.Configuration);
        builder.Services.AddSocketRules();
        builder.Services.AddSingleton(TimeProvider.System);
        var app = builder.Build();
        app.UseWebSockets();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/s", (HttpContext context, System.Security.Claims.ClaimsPrincipal user) => SocketSession.RunAsync(
            context, user,
            caller =>
            {
                var client = new SocketClient(caller);
                opened.SetResult(client);
                return client;
            },
            client => client.Drop())).RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task REQ_BT_028_Dropped_client_is_closed_1013_too_slow_after_its_frames()
    {
        var opened = new TaskCompletionSource<SocketClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await HostAsync(opened);

        using var socket = await Sockets.OpenAsync(app.GetTestServer(), "/s", Sockets.CookieFor("viewer"));
        var client = await opened.Task;
        client.TrySend("{\"type\":\"last\"}"u8.ToArray());
        client.Drop();

        Assert.Equal("last", (await Sockets.ReceiveAsync(socket)).GetProperty("type").GetString());
        var (status, reason) = await Sockets.WaitForCloseAsync(socket, TimeSpan.FromSeconds(5));
        Assert.Equal(1013, (int?)status);
        Assert.Equal("too slow", reason);
    }

    /// <summary>The limit counts what waits, not what has gone: a healthy client passes through it many times over.</summary>
    [Fact]
    public async Task REQ_BT_028_A_client_that_keeps_reading_outlasts_the_backlog_limit()
    {
        var opened = new TaskCompletionSource<SocketClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await HostAsync(opened);
        using var socket = await Sockets.OpenAsync(app.GetTestServer(), "/s", Sockets.CookieFor("viewer"));
        var client = await opened.Task;
        var frame = "{\"type\":\"next\",\"pad\":\""u8.ToArray().Concat(Enumerable.Repeat((byte)0x78, 32 * 1024)).Concat("\"}"u8.ToArray()).ToArray();

        for (var i = 0; i < 3 * SocketRules.MaxBacklogBytes / frame.Length; i++)
        {
            Assert.True(client.TrySend(frame), $"frame {i}");
            await Sockets.ReceiveAsync(socket);
        }
    }

    /// <summary>Opens a socket the way a browser does, across a real TCP connection.</summary>
    private static async Task<ClientWebSocket> OpenRealAsync(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Cookie", Sockets.CookieFor("viewer"));
        socket.Options.SetRequestHeader("Origin", Env.Origin);
        await socket.ConnectAsync(new Uri(address.Replace("http", "ws") + "/s"), CancellationToken.None);
        return socket;
    }

    /// <summary>The test server hides this: over TCP, aborting at the end of the session threw away the close frame.</summary>
    [Fact]
    public async Task REQ_BT_028_On_a_real_listener_a_dropped_client_still_gets_1013()
    {
        var opened = new TaskCompletionSource<SocketClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await HostAsync(opened, realListener: true);

        using var socket = await OpenRealAsync(app);
        var client = await opened.Task;
        client.Drop();

        // A proxy reads after the server has moved on, and a reset discards what it has not read.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var (status, reason) = await Sockets.WaitForCloseAsync(socket, TimeSpan.FromSeconds(5));
        Assert.Equal(1013, (int?)status);
        Assert.Equal("too slow", reason);
    }
}
