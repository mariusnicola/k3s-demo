using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

/// <summary>Every /ws client gets its own queue, so a slow client loses old frames and holds nobody else up.</summary>
public sealed class SocketHub(JsonSerializerOptions json)
{
    private const int QueuedFrames = 256;
    private const string ExpiredReason = "unauthorized";
    private const WebSocketCloseStatus Expired = (WebSocketCloseStatus)4401;
    private static readonly TimeSpan SendLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CloseLimit = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<Guid, Client> clients = new();

    private sealed record Client(Channel<byte[]> Queue, bool Admin);

    /// <summary>A frame only admins get is not worth a Prometheus read with none listening.</summary>
    public bool AdminOpen => clients.Values.Any(c => c.Admin);

    /// <summary>Admins only unless a frame says otherwise, so a new frame cannot reach an anonymous socket by default.</summary>
    public void Broadcast(object frame) => Send(frame, clients.Values.Where(c => c.Admin));

    /// <summary>For the cluster work banner, which every visitor sees.</summary>
    public void BroadcastPublic(object frame) => Send(frame, clients.Values);

    private void Send(object frame, IEnumerable<Client> targets)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, frame.GetType(), json);
        foreach (var client in targets) client.Queue.Writer.TryWrite(bytes);
    }

    /// <summary>With adminUntil the socket gets every frame until then and closes 4401, as Live does. Without it, public frames only.</summary>
    public async Task Run(WebSocket socket, IEnumerable<object> firstFrames, DateTimeOffset? adminUntil, CancellationToken aborted)
    {
        var id = Guid.NewGuid();
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueuedFrames) { FullMode = BoundedChannelFullMode.DropOldest });
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var expired = adminUntil is { } until ? Task.Delay(Left(until), ending.Token) : Task.Delay(Timeout.Infinite, ending.Token);
        clients[id] = new Client(queue, adminUntil is not null);
        try
        {
            foreach (var frame in firstFrames) queue.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(frame, frame.GetType(), json));
            await Pump(socket, queue, expired, socket.ReceiveAsync(new byte[256], aborted), aborted);
        }
        finally
        {
            clients.TryRemove(id, out _);
            await ending.CancelAsync();
        }
    }

    /// <summary>Sends queued frames until the socket closes or the session ends.</summary>
    private static async Task Pump(WebSocket socket, Channel<byte[]> queue, Task expired, Task closed, CancellationToken aborted)
    {
        while (socket.State == WebSocketState.Open)
        {
            var ready = queue.Reader.WaitToReadAsync(aborted).AsTask();
            // Expiry is listed first, so it wins over a frame that is ready at the same moment.
            var woke = await Task.WhenAny(expired, closed, ready);
            if (woke == closed) return;
            if (woke == expired)
            {
                await Close(socket);
                return;
            }
            while (!expired.IsCompleted && queue.Reader.TryRead(out var bytes))
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                limit.CancelAfter(SendLimit);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, limit.Token);
            }
        }
    }

    private static TimeSpan Left(DateTimeOffset until)
    {
        var left = until - DateTimeOffset.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static async Task Close(WebSocket socket)
    {
        using var limit = new CancellationTokenSource(CloseLimit);
        try
        {
            await socket.CloseOutputAsync(Expired, ExpiredReason, limit.Token);
        }
        catch (Exception problem) when (problem is OperationCanceledException or WebSocketException)
        {
            socket.Abort();
        }
    }
}
