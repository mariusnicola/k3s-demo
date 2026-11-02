using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

/// <summary>Caps sockets before they are accepted, because the monitor's memory limit holds a queue for each one.</summary>
public sealed class SocketLimits
{
    /// <summary>A place frees only when another socket closes, so a short wait would just bring the next refusal.</summary>
    public const int CapWaitSeconds = 30;

    private readonly int perClient;
    private readonly int total;
    private readonly IReadOnlyList<System.Net.IPNetwork> proxies;
    private readonly PartitionedRateLimiter<string> upgrades;
    private readonly Dictionary<string, int> open = [];
    private int count;

    public SocketLimits(int perClient, int total, int burst, int perSecond, string? trusted)
    {
        this.perClient = perClient;
        this.total = total;
        proxies = (trusted ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(System.Net.IPNetwork.Parse).ToList();
        upgrades = Buckets(burst, perSecond);
    }

    /// <summary>One token bucket per client.</summary>
    public static PartitionedRateLimiter<string> Buckets(int burst, int perSecond) =>
        PartitionedRateLimiter.Create<string, string>(client => RateLimitPartition.GetTokenBucketLimiter(client, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = burst,
            TokensPerPeriod = perSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            AutoReplenishment = true,
            QueueLimit = 0
        }));

    /// <summary>Whole seconds above zero, for Retry-After.</summary>
    public static int WaitSeconds(RateLimitLease lease) =>
        lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)) : 1;

    public static SocketLimits Standard(string? trusted) => new(perClient: 10, total: 100, burst: 10, perSecond: 5, trusted);

    /// <summary>A client can write the forwarded header, so it counts only from a trusted proxy, and then only its last entry, which the proxy wrote.</summary>
    public string Client(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (peer is { IsIPv4MappedToIPv6: true }) peer = peer.MapToIPv4();
        var believed = peer is not null && proxies.Any(p => p.Contains(peer));
        var forwarded = believed ? context.Request.Headers["X-Forwarded-For"].ToString() : "";
        var last = forwarded[(forwarded.LastIndexOf(',') + 1)..].Trim();
        return last.Length > 0 ? last : peer?.ToString() ?? "unknown";
    }

    /// <summary>Takes a place and returns null, or returns the seconds to wait. A null must be paired with Leave.</summary>
    public int? Refuse(string client)
    {
        using var lease = upgrades.AttemptAcquire(client);
        if (!lease.IsAcquired) return WaitSeconds(lease);
        lock (open)
        {
            var held = open.GetValueOrDefault(client);
            if (count >= total || held >= perClient) return CapWaitSeconds;
            open[client] = held + 1;
            count++;
            return null;
        }
    }

    public void Leave(string client)
    {
        lock (open)
        {
            count--;
            if (open[client] == 1) open.Remove(client);
            else open[client]--;
        }
    }
}
