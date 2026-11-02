using System.Globalization;
using System.Net.WebSockets;
using System.Security.Claims;
using BatchTracker.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;

namespace BatchTracker.Infrastructure;

public static class SocketSession
{
    public static async Task RunAsync(
        HttpContext context, ClaimsPrincipal user, Func<Caller, SocketClient> connect, Action<SocketClient> disconnect)
    {
        if (await RefusedAsync(context)) return;

        var services = context.RequestServices;
        var caller = user.ToCaller();
        var gate = services.GetRequiredService<SocketGate>();
        if (!gate.TryEnter(caller.User))
        {
            context.Response.Headers.RetryAfter = SocketRules.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            await RefuseAsync(context, StatusCodes.Status429TooManyRequests, "Too many open sockets.");
            return;
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await PumpAsync(context, socket, connect(caller), disconnect, Remaining(user, services.GetRequiredService<TimeProvider>()), services.GetRequiredService<TimeProvider>());
        }
        finally
        {
            gate.Leave(caller.User);
        }
    }

    /// <summary>Answers a request that may not open a socket. True when it was answered.</summary>
    private static async Task<bool> RefusedAsync(HttpContext context)
    {
        var services = context.RequestServices;
        if (!OriginRule.Allows(context.Request, services.GetRequiredService<SessionSettings>()))
        {
            await RefuseAsync(context, StatusCodes.Status403Forbidden, "Origin not allowed.");
            return true;
        }
        if (services.GetRequiredService<ShutdownState>().Draining)
        {
            context.Response.Headers.RetryAfter = SocketRules.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "Service stopping.");
            return true;
        }
        if (context.WebSockets.IsWebSocketRequest) return false;

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return true;
    }

    private static async Task PumpAsync(
        HttpContext context, WebSocket socket, SocketClient client, Action<SocketClient> disconnect, TimeSpan left, TimeProvider time)
    {
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        using var expiry = new CancellationTokenSource(left, time);
        var stopping = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ended.Token, expiry.Token, stopping);
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(ended.Token);
        var listening = ListenAsync(socket, ended);
        try
        {
            await ServeAsync(socket, client, waiting.Token, sending, ended.Token, expiry.Token, stopping);
        }
        finally
        {
            disconnect(client);

            // Cancelling a receive aborts the socket, and that reset wipes a close frame a proxy has not read yet. So the client gets time to answer it.
            ended.CancelAfter(SocketRules.CloseLimit);
            await listening;
        }
    }

    /// <summary>Sends until the outbox ends or a token fires, then closes with the code that says why.</summary>
    private static async Task ServeAsync(
        WebSocket socket, SocketClient client, CancellationToken waiting, CancellationTokenSource sending,
        CancellationToken ended, CancellationToken expiry, CancellationToken stopping)
    {
        try
        {
            await SendFramesAsync(socket, client, waiting, sending);

            // Only a dropped client ends the outbox.
            await CloseAsync(socket, SocketRules.TooSlow, SocketRules.TooSlowReason);
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested && !ended.IsCancellationRequested)
        {
            await CloseAsync(socket, SocketRules.Expired, SocketRules.ExpiredReason);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested && !ended.IsCancellationRequested)
        {
            await CloseAsync(socket, SocketRules.Stopping, SocketRules.StoppingReason);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
        }
    }

    /// <summary>Expiry stops the wait and never a send: a cancelled send kills the socket before its close code goes out.</summary>
    private static async Task SendFramesAsync(WebSocket socket, SocketClient client, CancellationToken waiting, CancellationTokenSource sending)
    {
        await foreach (var frame in client.Outbox.ReadAllAsync(waiting))
        {
            sending.CancelAfter(SocketRules.SendLimit);
            await socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, sending.Token);
            sending.CancelAfter(Timeout.InfiniteTimeSpan);
            client.Sent(frame);
        }
    }

    private static TimeSpan Remaining(ClaimsPrincipal user, TimeProvider time)
    {
        var expires = long.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Exp)!, CultureInfo.InvariantCulture);
        var left = DateTimeOffset.FromUnixTimeSeconds(expires) - time.GetUtcNow();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static Task RefuseAsync(HttpContext context, int status, string detail) =>
        Results.Problem(statusCode: status, detail: detail).ExecuteAsync(context);

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        using var limit = new CancellationTokenSource(SocketRules.CloseLimit);
        try
        {
            await socket.CloseOutputAsync(status, reason, limit.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            socket.Abort();
        }
    }

    private static async Task ListenAsync(WebSocket socket, CancellationTokenSource ended)
    {
        var buffer = new byte[256];
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer, ended.Token);
                if (received.MessageType != WebSocketMessageType.Close) continue;
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                break;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
        }
        if (!ended.IsCancellationRequested) await ended.CancelAsync();
    }
}
