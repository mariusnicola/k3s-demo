using System.Net.WebSockets;
using System.Text.Json;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.Line;

/// <summary>Holds one socket to Live open, so the walks hear an event finish instead of reading for it.</summary>
public sealed class LineSocket(LineApi api, LineSettled settled, LineOptions options, ILogger<LineSocket> logger) : BackgroundService
{
    private sealed record Frame(string? Type, string? ETag, Batch? Batch);

    /// <summary>The Line runs outside the cluster, so it reaches Live by the ingress that serves the API.</summary>
    public static Uri UrlFor(string apiUrl) => new(apiUrl.TrimEnd('/').Replace("http", "ws", StringComparison.Ordinal) + "/ws/hops");

    public static Func<string, CancellationToken, Task<WebSocket>> Through(string apiUrl) => async (bearer, cancel) =>
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {bearer}");
        await socket.ConnectAsync(UrlFor(apiUrl), cancel);
        return socket;
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.OpenSocket is not { } open) return;
        while (true)
        {
            try
            {
                using var socket = await open(await api.BearerAsync(stoppingToken), stoppingToken);
                settled.Connected = true;
                await ReadAsync(socket, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("The Line has no socket to Live, so it reads instead: {Reason}", e.GetBaseException().Message);
            }
            finally
            {
                settled.Connected = false;
            }
            await Task.Delay(options.SocketRetry, stoppingToken);
        }
    }

    private async Task ReadAsync(WebSocket socket, CancellationToken cancel)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var part = await socket.ReceiveAsync(buffer, cancel);
            if (part.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, part.Count);
            if (!part.EndOfMessage) continue;
            Apply(JsonSerializer.Deserialize<Frame>(message.GetBuffer().AsSpan(0, (int)message.Length), JsonDefaults.Options));
            message.SetLength(0);
        }
    }

    private void Apply(Frame? frame)
    {
        if (frame is { Type: "batch", ETag: { } etag, Batch: { } batch }) settled.Saw(etag, batch);
    }
}
