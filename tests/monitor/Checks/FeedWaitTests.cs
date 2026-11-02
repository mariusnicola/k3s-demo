using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>One connect, one socket: nothing is tried again, and a connect that never answers is named.</summary>
public class FeedWaitTests
{
    private const string Frame = "{\"type\":\"operations\",\"operations\":[\"wanted\"]}";
    private static readonly Regex Wanted = new("wanted");

    private int requests;

    private async Task<(WebApplication App, string Url)> Serve(RequestDelegate answer)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", context =>
        {
            Interlocked.Increment(ref requests);
            return answer(context);
        });
        await app.StartAsync();
        var origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, origin.Replace("http://", "ws://") + "/ws");
    }

    private static Task<string> Wait(string url, CancellationToken token) => BrowserPaths.Wait(url, "", "session", Wanted, null, token);

    private static async Task Silent(HttpContext context)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task Sends(HttpContext context, TimeSpan after, string frame)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await Task.Delay(after, context.RequestAborted);
        await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, CancellationToken.None);
        await socket.ReceiveAsync(new byte[16], context.RequestAborted);
    }

    [Fact]
    public async Task REQ_BT_054_The_frame_is_found_on_the_one_connect()
    {
        var (app, url) = await Serve(c => Sends(c, TimeSpan.Zero, Frame));
        await using var _ = app;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Assert.Equal(Frame, await Wait(url, limit.Token));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task REQ_BT_054_A_connect_with_no_answer_is_not_tried_again_and_says_so_inside_the_limit()
    {
        var (app, url) = await Serve(Silent);
        await using var _ = app;
        using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
        var clock = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Wait(url, limit.Token));

        Assert.Equal($"no connection to {url}", failure.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"{clock.Elapsed}");
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task REQ_BT_054_A_slow_connect_is_left_to_finish()
    {
        var (app, url) = await Serve(async c =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), c.RequestAborted);
            await Sends(c, TimeSpan.Zero, Frame);
        });
        await using var _ = app;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Assert.Equal(Frame, await Wait(url, limit.Token));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task REQ_BT_054_A_connected_socket_is_not_tried_again_when_the_frame_never_comes()
    {
        var (app, url) = await Serve(c => Sends(c, TimeSpan.FromSeconds(30), Frame));
        await using var _ = app;
        using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));

        var failure = await Record.ExceptionAsync(() => Wait(url, limit.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task REQ_BT_054_A_refused_socket_fails_at_once_and_is_not_tried_again()
    {
        var (app, url) = await Serve(c =>
        {
            c.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        });
        await using var _ = app;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<WebSocketException>(() => Wait(url, limit.Token));

        Assert.Equal(1, requests);
    }
}
