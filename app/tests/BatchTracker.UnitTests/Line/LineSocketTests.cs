using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace BatchTracker.UnitTests;

/// <summary>A real socket against a real server, because the frames that matter are the ones a real socket splits.</summary>
public sealed class LineSocketTests : LineFailureTest, IAsyncDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly LineSettled settled = new();
    private readonly ConcurrentQueue<IHeaderDictionary> requests = new();
    private readonly CancellationTokenSource stop = new(Limit);
    private WebApplication? server;
    private LineSocket? socket;
    private int connections;

    /// <summary>Runs the Line's socket against a server that plays `script` on each connection, numbered from 1.</summary>
    private async Task StartAsync(
        Func<WebSocket, int, Task> script, TimeSpan? retry = null, Func<HttpRequestMessage, Task<HttpResponseMessage>>? login = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        server = builder.Build();
        server.UseWebSockets();
        server.Map("/ws/hops", async (HttpContext context) =>
        {
            requests.Enqueue(context.Request.Headers);
            using var open = await context.WebSockets.AcceptWebSocketAsync();
            await script(open, Interlocked.Increment(ref connections));
        });
        await server.StartAsync();

        var options = new LineOptions
        {
            SocketRetry = retry ?? TimeSpan.FromMilliseconds(10),
            OpenSocket = LineSocket.Through(server.Urls.First() + "/")
        };
        var http = new HttpClient(new Wire(login ?? (_ => Login()))) { BaseAddress = new Uri("http://api.test/api/") };
        socket = new LineSocket(new LineApi(http, options, TimeProvider.System, settled), settled, options, NullLogger<LineSocket>.Instance);
        await socket.StartAsync(stop.Token);
    }

    private async Task<bool> EventuallyAsync(Func<bool> check)
    {
        while (!stop.IsCancellationRequested)
        {
            if (check()) return true;
            await Task.Delay(5);
        }
        return check();
    }

    private static byte[] Frame(string etag, long version) =>
        JsonSerializer.SerializeToUtf8Bytes(new { type = "batch", etag, batch = NewBatch(null) with { Version = version } }, JsonDefaults.Options);

    private static Task SendAsync(WebSocket open, byte[] frame) =>
        open.SendAsync(frame, WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Keeps the server's end open until the test is over.</summary>
    private static async Task HoldAsync(WebSocket open)
    {
        try
        {
            await open.ReceiveAsync(new byte[16], CancellationToken.None);
        }
        catch (WebSocketException)
        {
        }
    }

    [Fact]
    public async Task REQ_BT_045_The_socket_opens_with_the_bearer_and_no_origin()
    {
        await StartAsync((open, _) => HoldAsync(open));

        Assert.True(await EventuallyAsync(() => !requests.IsEmpty));

        requests.TryPeek(out var headers);
        Assert.Equal($"Bearer {Token}", headers!.Authorization.ToString());
        Assert.False(headers.ContainsKey("Origin"));
    }

    [Fact]
    public async Task REQ_BT_045_A_batch_frame_in_parts_after_a_snapshot_ends_a_wait()
    {
        using var watch = settled.Watch(BatchId);
        await StartAsync(async (open, _) =>
        {
            await SendAsync(open, JsonSerializer.SerializeToUtf8Bytes(new { type = "snapshot", batches = new[] { new { etag = "\"s\"" } } }));
            var frame = Frame("\"frame\"", 9);
            await open.SendAsync(frame.AsMemory(0, 20), WebSocketMessageType.Text, false, CancellationToken.None);
            await open.SendAsync(frame.AsMemory(20), WebSocketMessageType.Text, true, CancellationToken.None);
            await HoldAsync(open);
        });

        var seen = await watch.AfterAsync(8, Limit, stop.Token);

        Assert.Equal("\"frame\"", seen?.ETag);
    }

    [Fact]
    public async Task REQ_BT_045_Connected_holds_only_while_the_socket_is_open()
    {
        var close = new TaskCompletionSource();
        await StartAsync(async (open, _) =>
        {
            await close.Task;
            await open.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }, retry: TimeSpan.FromSeconds(30));

        Assert.True(await EventuallyAsync(() => settled.Connected));
        close.SetResult();

        Assert.True(await EventuallyAsync(() => !settled.Connected));
    }

    [Fact]
    public async Task REQ_BT_045_A_dropped_socket_is_opened_again()
    {
        using var watch = settled.Watch(BatchId);
        await StartAsync(async (open, connection) =>
        {
            if (connection == 1) return;
            await SendAsync(open, Frame("\"second\"", 9));
            await HoldAsync(open);
        });

        Assert.Equal("\"second\"", (await watch.AfterAsync(8, Limit, stop.Token))?.ETag);
    }

    [Fact]
    public async Task REQ_BT_045_A_frame_that_is_not_JSON_costs_one_reconnect_not_the_socket()
    {
        using var watch = settled.Watch(BatchId);
        await StartAsync(async (open, connection) =>
        {
            await SendAsync(open, connection == 1 ? "<html>"u8.ToArray() : Frame("\"good\"", 9));
            await HoldAsync(open);
        });

        Assert.Equal("\"good\"", (await watch.AfterAsync(8, Limit, stop.Token))?.ETag);
    }

    [Fact]
    public async Task REQ_BT_045_A_failed_sign_in_is_tried_again()
    {
        var logins = 0;
        await StartAsync((open, _) => HoldAsync(open), login: _ => Interlocked.Increment(ref logins) == 1
            ? Json(HttpStatusCode.ServiceUnavailable, new { detail = "starting" })
            : Login());

        Assert.True(await EventuallyAsync(() => settled.Connected));
        Assert.Equal(2, logins);
    }

    [Fact]
    public async Task REQ_BT_045_With_no_way_to_open_a_socket_the_Line_only_reads()
    {
        var options = new LineOptions();
        var http = new HttpClient(new Wire(_ => Login())) { BaseAddress = new Uri("http://api.test/api/") };
        using var quiet = new LineSocket(new LineApi(http, options, TimeProvider.System, settled), settled, options, NullLogger<LineSocket>.Instance);

        await quiet.StartAsync(stop.Token);
        await quiet.ExecuteTask!;

        Assert.False(settled.Connected);
    }

    [Theory]
    [InlineData("http://192.0.2.10", "ws://192.0.2.10/ws/hops")]
    [InlineData("https://line.test/", "wss://line.test/ws/hops")]
    public void REQ_BT_045_The_socket_goes_where_the_API_goes(string api, string socketUrl)
    {
        Assert.Equal(new Uri(socketUrl), LineSocket.UrlFor(api));
    }

    public async ValueTask DisposeAsync()
    {
        if (socket is not null) await socket.StopAsync(CancellationToken.None);
        socket?.Dispose();
        if (server is not null) await server.DisposeAsync();
        stop.Dispose();
    }
}
