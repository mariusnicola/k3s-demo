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

public static class Sockets
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public static string TokenFor(string user, TimeProvider? time = null) =>
        new TokenIssuer(Env.PrivateKey, time ?? TimeProvider.System).Issue(DemoUsers.Find(user)!);

    public static string CookieFor(string user, TimeProvider? time = null) => $"{SessionCookie.Name}={TokenFor(user, time)}";

    public static Task<WebSocket> OpenAsync(TestServer server, string path, string? cookie, string? origin = Env.Origin, string? bearer = null)
    {
        var client = server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (cookie is not null) request.Headers["Cookie"] = cookie;
            if (origin is not null) request.Headers["Origin"] = origin;
            if (bearer is not null) request.Headers["Authorization"] = $"Bearer {bearer}";
        };
        return client.ConnectAsync(new Uri($"ws://localhost{path}"), CancellationToken.None);
    }

    public static async Task<string> RefusalAsync(TestServer server, string path, string? cookie, string? origin = Env.Origin, string? bearer = null)
    {
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(server, path, cookie, origin, bearer));
        return refused.Message;
    }

    public static async Task<JsonElement> ReceiveAsync(WebSocket socket, TimeSpan? limit = null)
    {
        using var timer = new CancellationTokenSource(limit ?? Limit);
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, timer.Token);
            if (received.MessageType == WebSocketMessageType.Close) throw new WebSocketException("closed");
            message.Write(buffer, 0, received.Count);
            if (!received.EndOfMessage) continue;
            return JsonDocument.Parse(message.ToArray()).RootElement.Clone();
        }
    }

    public static async Task<JsonElement> ReceiveTypeAsync(WebSocket socket, string type)
    {
        using var timer = new CancellationTokenSource(Limit);
        while (true)
        {
            timer.Token.ThrowIfCancellationRequested();
            var frame = await ReceiveAsync(socket);
            if (frame.GetProperty("type").GetString() == type) return frame;
        }
    }

    public static async Task<(WebSocketCloseStatus? Status, string? Reason)> WaitForCloseAsync(WebSocket socket, TimeSpan limit)
    {
        using var timer = new CancellationTokenSource(limit);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, timer.Token);
            if (received.MessageType == WebSocketMessageType.Close) return (received.CloseStatus, received.CloseStatusDescription);
        }
    }
}
