using System.Net;

namespace BatchTracker.Infrastructure;

public static class RetryRules
{
    public const int MaxAttempts = 10;
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>All nine waits at the top of the jitter.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(81);

    /// <summary>Equal jitter: half fixed so the attempts still span a restart, half random so callers spread out.</summary>
    public static TimeSpan Backoff(int attempt, double random)
    {
        var delay = Math.Min(FirstDelay.TotalMilliseconds * Math.Pow(2, attempt - 1), MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(delay / 2 + delay * random / 2);
    }

    public static bool IsTransient(HttpStatusCode status) => status is
        HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}

/// <summary>
/// A POST goes once unless its caller declares it safe, because a lost reply says nothing about whether it applied.
/// </summary>
public static class RetryRequest
{
    private static readonly HttpRequestOptionsKey<bool> Safe = new("retry.safe");
    private static readonly HttpRequestOptionsKey<bool> Once = new("retry.once");
    private static readonly HttpRequestOptionsKey<TimeSpan> Limit = new("retry.timeout");
    private static readonly HttpRequestOptionsKey<int> Attempts = new("retry.attempts");
    private static readonly HttpRequestOptionsKey<int> Throttles = new("retry.throttles");

    public static HttpRequestMessage SafeToRepeat(this HttpRequestMessage request)
    {
        request.Options.Set(Safe, true);
        return request;
    }

    public static HttpRequestMessage SingleAttempt(this HttpRequestMessage request)
    {
        request.Options.Set(Once, true);
        return request;
    }

    public static HttpRequestMessage WithAttemptTimeout(this HttpRequestMessage request, TimeSpan timeout)
    {
        request.Options.Set(Limit, timeout);
        return request;
    }

    public static int AttemptsMade(this HttpRequestMessage request) => request.Options.TryGetValue(Attempts, out var count) ? count : 0;

    public static int TimesThrottled(this HttpRequestMessage request) => request.Options.TryGetValue(Throttles, out var count) ? count : 0;

    internal static int AttemptLimit(this HttpRequestMessage request)
    {
        if (request.Options.TryGetValue(Once, out _)) return 1;
        var repeatable = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head || request.Options.TryGetValue(Safe, out _);
        return repeatable ? RetryRules.MaxAttempts : 1;
    }

    internal static TimeSpan AttemptTimeout(this HttpRequestMessage request) =>
        request.Options.TryGetValue(Limit, out var timeout) ? timeout : RetryRules.AttemptTimeout;

    internal static void Record(this HttpRequestMessage request, int attempts, int throttles)
    {
        request.Options.Set(Attempts, attempts);
        request.Options.Set(Throttles, throttles);
    }
}
