using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BatchTracker.Infrastructure;

public sealed class RetryHandler(
    Func<double> random, Func<TimeSpan, CancellationToken, Task> delay, Func<DateTimeOffset> now, ILogger log) : DelegatingHandler
{
    /// <summary>The query never appears: nothing here may carry a token or a secret into a log.</summary>
    public static string Name(HttpRequestMessage request) =>
        $"{request.Method} {(request.RequestUri is { IsAbsoluteUri: true } uri ? uri.AbsolutePath : "/" + request.RequestUri?.OriginalString.Split('?')[0])}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(token);
        var started = now();
        var throttles = 0;

        for (var attempt = 1; ; attempt++)
        {
            var (response, failure) = await TryAsync(request, body, token);
            if (response is { StatusCode: HttpStatusCode.TooManyRequests }) throttles++;
            request.Record(attempt, throttles);

            var hint = response is null ? null : RetryAfter(response);
            if (Ended(request, response, failure, hint, attempt, started) is { } last) return last;

            response?.Dispose();
            var backoff = RetryRules.Backoff(attempt, random());
            await delay(hint > backoff ? hint.Value : backoff, token);
        }
    }

    /// <summary>The answer this attempt ends on, or null when another attempt follows.</summary>
    private HttpResponseMessage? Ended(
        HttpRequestMessage request, HttpResponseMessage? response, Exception? failure, TimeSpan? hint, int attempt, DateTimeOffset started)
    {
        if (response is not null && !RetryRules.IsTransient(response.StatusCode)) return Finish(response, request);
        if (hint > RetryRules.MaxDelay)
        {
            log.LogWarning("{Call} stopped after {Attempts} attempts: told to wait {Wait} s, past the longest delay",
                Name(request), attempt, (int)hint.Value.TotalSeconds);
            return Finish(response!, request);
        }

        var limit = request.AttemptLimit();
        if (attempt < limit) return null;
        if (limit == 1 && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return limit == 1 ? Finish(response!, request) : GiveUp(request, attempt, started, response, failure);
    }

    private async Task<(HttpResponseMessage? Response, Exception? Failure)> TryAsync(HttpRequestMessage request, byte[]? body, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(request.AttemptTimeout());
        using var copy = Copy(request, body);
        try
        {
            return (await base.SendAsync(copy, timeout.Token), null);
        }
        catch (Exception e) when (!token.IsCancellationRequested && e is HttpRequestException or OperationCanceledException or IOException or SocketException)
        {
            return (null, e);
        }
    }

    private HttpResponseMessage GiveUp(HttpRequestMessage request, int attempts, DateTimeOffset started, HttpResponseMessage? response, Exception? failure)
    {
        var seconds = (now() - started).TotalSeconds;
        var reason = response is null ? failure!.Message : $"HTTP {(int)response.StatusCode}";
        log.LogWarning("{Call} failed after {Attempts} attempts in {Seconds:0.#} s: {Reason}", Name(request), attempts, seconds, reason);
        if (response is not null) return Finish(response, request);
        throw new HttpRequestException($"{Name(request)} failed after {attempts} attempts in {seconds:0.#} s: {reason}", failure);
    }

    private static HttpResponseMessage Finish(HttpResponseMessage response, HttpRequestMessage request)
    {
        response.RequestMessage = request;
        return response;
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - now(),
        _ => null
    };

    private static HttpRequestMessage Copy(HttpRequestMessage request, byte[]? body)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var (name, values) in request.Headers) copy.Headers.TryAddWithoutValidation(name, values);
        if (body is null) return copy;

        copy.Content = new ByteArrayContent(body);
        foreach (var (name, values) in request.Content!.Headers) copy.Content.Headers.TryAddWithoutValidation(name, values);
        return copy;
    }
}

public static class RetrySetup
{
    /// <summary>The handler owns time limits per attempt, so a client-wide timeout cannot cut the retries short.</summary>
    public static IHttpClientBuilder AddRetries(this IHttpClientBuilder builder) => builder
        .ConfigureHttpClient(http => http.Timeout = Timeout.InfiniteTimeSpan)
        .AddHttpMessageHandler(() => new CorrelationHandler())
        .AddHttpMessageHandler(services =>
        {
            var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
            return new RetryHandler(
                Random.Shared.NextDouble,
                (wait, token) => Task.Delay(wait, time, token),
                time.GetUtcNow,
                services.GetRequiredService<ILoggerFactory>().CreateLogger<RetryHandler>());
        });
}
