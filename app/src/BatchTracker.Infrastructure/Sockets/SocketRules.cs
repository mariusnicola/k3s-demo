using System.Net.WebSockets;
using System.Threading.Channels;
using BatchTracker.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.Infrastructure;

public static class SocketRules
{
    public const int MaxPerUser = 5;
    /// <summary>Bytes, not frames: frames are what a throttled pod stacks up in a stall, and a count that low took a healthy client for a slow one. A client that really stopped reading is caught by SendLimit; this bounds its memory until then.</summary>
    public const int MaxBacklogBytes = 1024 * 1024;
    public const int RetryAfterSeconds = 30;
    public const WebSocketCloseStatus Expired = (WebSocketCloseStatus)4401;
    public const WebSocketCloseStatus TooSlow = (WebSocketCloseStatus)1013;
    public const string ExpiredReason = "unauthorized";
    public const string TooSlowReason = "too slow";
    public const WebSocketCloseStatus Stopping = WebSocketCloseStatus.EndpointUnavailable;
    public const string StoppingReason = "service stopping";
    public static readonly TimeSpan Ping = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SendLimit = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CloseLimit = TimeSpan.FromSeconds(2);

    public static IServiceCollection AddSocketRules(this IServiceCollection services)
    {
        services.TryAddSingleton<ShutdownState>();
        services.AddSingleton<SocketGate>();
        return services.Configure<WebSocketOptions>(o => o.KeepAliveInterval = Ping);
    }
}

public sealed class SocketClient(Caller caller)
{
    private readonly Channel<byte[]> outbox = Channel.CreateUnbounded<byte[]>();
    private long queued;

    public Caller Caller { get; } = caller;
    public ChannelReader<byte[]> Outbox => outbox.Reader;

    /// <summary>The frame an empty outbox takes is always allowed, because a snapshot may be larger than the limit.</summary>
    public bool TrySend(byte[] frame)
    {
        var total = Interlocked.Add(ref queued, frame.Length);
        if ((total <= SocketRules.MaxBacklogBytes || total == frame.Length) && outbox.Writer.TryWrite(frame)) return true;
        Interlocked.Add(ref queued, -frame.Length);
        return false;
    }

    /// <summary>Frees the frame's share of the limit once it has gone out.</summary>
    public void Sent(byte[] frame) => Interlocked.Add(ref queued, -frame.Length);

    public void Drop() => outbox.Writer.TryComplete();
}

/// <summary>Counts open sockets per user on this pod, so one user cannot hold unbounded memory.</summary>
public sealed class SocketGate
{
    private readonly object sync = new();
    private readonly Dictionary<string, int> open = [];

    public bool TryEnter(string user)
    {
        lock (sync)
        {
            var count = open.GetValueOrDefault(user);
            if (count >= SocketRules.MaxPerUser) return false;
            open[user] = count + 1;
            return true;
        }
    }

    public void Leave(string user)
    {
        lock (sync)
        {
            if (open[user] == 1) open.Remove(user);
            else open[user]--;
        }
    }
}
