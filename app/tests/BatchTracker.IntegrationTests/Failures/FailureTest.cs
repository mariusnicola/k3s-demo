using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

/// <summary>How much earlier than asked a timed wait can read on the Stopwatch.</summary>
public static class Timing
{
    /// <summary>One Windows timer tick: a Task.Delay ends on the timer's clock, so its full length can read a hair short, and a floor must not fail on that.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(16);
}

/// <summary>Hosts with the real delay on a short clock, and the clients that fail against them.</summary>
public abstract class FailureTest(Stack stack) : StackTest(stack)
{
    protected static readonly TimeSpan Step = TimeSpan.FromMilliseconds(300);

    /// <summary>Same shape as the real rules on a shorter clock, so the sequence runs in seconds.</summary>
    protected static readonly FailureRules Short = new() { FirstDelay = Step, MaxDelay = 4 * Step };

    private readonly List<IAsyncDisposable> hosts = [];

    public override async Task DisposeAsync()
    {
        foreach (var host in hosts) await host.DisposeAsync();
    }

    protected WebApplicationFactory<T> Delayed<T>(WebApplicationFactory<T> host, FailureRules? rules = null) where T : class
    {
        var delayed = host.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<FailureRules>().AddSingleton(rules ?? Short)));
        hosts.Add(delayed);

        // A cold host takes longer than a step to answer its first request.
        delayed.CreateClient().GetAsync("/healthz").GetAwaiter().GetResult().EnsureSuccessStatusCode();
        return delayed;
    }

    protected WebApplicationFactory<BatchTracker.Api.ApiMarker> Api(FailureRules? rules = null) => Delayed(Stack.Api, rules);

    protected static HttpClient From<T>(WebApplicationFactory<T> host, string address = "203.0.113.20", string? bearer = null) where T : class
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", address);
        if (bearer is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    protected static async Task<(HttpStatusCode Status, TimeSpan Took)> TimeAsync(Func<Task<HttpResponseMessage>> call)
    {
        var clock = Stopwatch.StartNew();
        var reply = await call();
        return (reply.StatusCode, clock.Elapsed);
    }

    protected static Task<HttpResponseMessage> Login(HttpClient client, string user) => client.PostAsJsonAsync("/api/auth/dev-login", new { user });

    protected static async Task SpendFreeFailuresAsync(HttpClient client, string path = "/api/summary")
    {
        for (var i = 0; i < 2; i++) await client.GetAsync(path);
    }

    protected static FailureLedger LedgerOf<T>(WebApplicationFactory<T> host) where T : class =>
        host.Services.GetRequiredService<FailureLedger>();
}
