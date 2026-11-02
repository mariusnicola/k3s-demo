using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

/// <summary>A held failure: seats while it waits, clients that leave, threads, and each service.</summary>
public class FailureHoldTests(Stack stack) : FailureDelayTest(stack)
{
    [Fact]
    public async Task REQ_BT_049_A_dropped_client_keeps_its_place_for_the_delay_and_its_count()
    {
        var host = Api(Short with { FirstDelay = 2 * Step, MaxDelay = 2 * Step });
        var client = From(host);
        await SpendFreeFailuresAsync(client);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("/api/summary", cancel.Token));

        await Task.Delay(1.4 * Step - clock.Elapsed);
        Assert.Equal(1, LedgerOf(host).WaitingOf("203.0.113.20"));
        while (LedgerOf(host).WaitingOf("203.0.113.20") > 0 && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
        Assert.Equal(0, LedgerOf(host).WaitingOf("203.0.113.20"));
        Assert.True(clock.Elapsed >= 2 * Step - Timing.Tick, $"{clock.Elapsed}");
        Assert.Equal(3, LedgerOf(host).FailuresOf("203.0.113.20"));
    }

    /// <summary>Ten seats held for the delay allow ten failures per delay, even from clients that never wait for the answer.</summary>
    [Fact]
    public async Task REQ_BT_049_Clients_that_hang_up_still_fail_at_the_seat_rate()
    {
        var rules = Short with { RefuseFrom = int.MaxValue, FirstDelay = Step, MaxDelay = Step };
        var host = Api(rules);
        var client = From(host);
        await SpendFreeFailuresAsync(client);
        var clock = Stopwatch.StartNew();
        var sent = 0;
        var hungUp = 0;

        while (clock.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
            try
            {
                await client.GetAsync("/api/summary", cancel.Token);
            }
            catch (OperationCanceledException)
            {
                hungUp++;
            }
            sent++;
        }

        Assert.True(hungUp > 0, $"{hungUp} of {sent}");

        var limit = (long)(rules.MaxWaiting * Math.Ceiling(clock.Elapsed / Step));
        Assert.True(sent > limit, $"{sent} against {limit}");
        Assert.InRange(LedgerOf(host).FailuresOf("203.0.113.20") - 2, 10, limit);
    }

    [Fact]
    public async Task REQ_BT_049_On_a_real_listener_a_dropped_client_keeps_its_place_for_the_delay()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddFailureDelays();
        builder.Services.RemoveAll<FailureRules>().AddSingleton(new FailureRules { FirstDelay = TimeSpan.FromSeconds(1) });
        await using var app = builder.Build();
        app.UseFailureDelays();
        app.MapGet("/refused", () => Results.Unauthorized());
        await app.StartAsync();
        var ledger = app.Services.GetRequiredService<FailureLedger>();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        await client.GetAsync("/refused");
        await client.GetAsync("/refused");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("/refused", cancel.Token));

        var key = ledger.Clients == 1 ? "127.0.0.1" : "";
        await Task.Delay(TimeSpan.FromMilliseconds(700) - clock.Elapsed);
        Assert.Equal(1, ledger.WaitingOf(key));
        while (ledger.WaitingOf(key) > 0 && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
        Assert.Equal(0, ledger.WaitingOf(key));
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1) - Timing.Tick, $"{clock.Elapsed}");
    }

    [Fact]
    public async Task REQ_BT_049_On_a_real_listener_a_client_that_leaves_before_the_route_answers_still_counts()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddFailureDelays();
        builder.Services.RemoveAll<FailureRules>().AddSingleton(new FailureRules { FirstDelay = TimeSpan.FromSeconds(1) });
        await using var app = builder.Build();
        app.UseFailureDelays();
        app.MapGet("/slow", async () =>
        {
            await Task.Delay(500);
            return Results.Unauthorized();
        });
        await app.StartAsync();
        var ledger = app.Services.GetRequiredService<FailureLedger>();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("/slow", cancel.Token));

        var start = Stopwatch.StartNew();
        while (ledger.FailuresOf("127.0.0.1") == 0 && start.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
        Assert.Equal(1, ledger.FailuresOf("127.0.0.1"));
    }

    [Fact]
    public async Task REQ_BT_049_Another_client_answers_at_once_while_one_sits_in_its_delay()
    {
        var host = Api(Short with { FirstDelay = TimeSpan.FromSeconds(2), MaxDelay = TimeSpan.FromSeconds(2) });
        var slow = From(host);
        await SpendFreeFailuresAsync(slow);
        var waiting = slow.GetAsync("/api/summary");
        await Task.Delay(100);

        var (status, took) = await TimeAsync(() => From(host, "203.0.113.23").GetAsync("/api/summary"));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.True(took < TimeSpan.FromSeconds(1), $"{took}");
        Assert.False(waiting.IsCompleted);
        await waiting;
    }

    [Fact]
    public async Task REQ_BT_049_Two_hundred_waiting_hold_no_threads()
    {
        var host = Api(Short with { FirstDelay = TimeSpan.FromSeconds(2), MaxDelay = TimeSpan.FromSeconds(2) });
        var clients = Enumerable.Range(1, 200).Select(i => From(host, $"10.9.{i / 100}.{i % 100}")).ToList();
        foreach (var client in clients) await SpendFreeFailuresAsync(client);
        var before = ThreadPool.ThreadCount;

        var waiting = clients.Select(c => c.GetAsync("/api/summary")).ToList();
        await Task.Delay(1000);
        var during = ThreadPool.ThreadCount;

        Assert.DoesNotContain(waiting, w => w.IsCompleted);
        Assert.True(during - before < 30, $"{before} to {during}");
        await Task.WhenAll(waiting);
    }

    [Fact]
    public async Task REQ_BT_049_Each_pod_counts_alone()
    {
        var one = Api();
        var two = Api();
        var first = From(one);
        var second = From(two);

        for (var i = 0; i < 2; i++)
        {
            await first.GetAsync("/api/summary");
            await second.GetAsync("/api/summary");
        }

        Assert.Equal(2, LedgerOf(one).FailuresOf("203.0.113.20"));
        Assert.Equal(2, LedgerOf(two).FailuresOf("203.0.113.20"));
    }

    [Fact]
    public async Task REQ_BT_049_Live_delays_its_401s_and_never_a_valid_internal_report()
    {
        await using var live = new LiveHost();
        var host = Delayed(live);
        var client = From(host);
        await SpendFreeFailuresAsync(client, "/ws/hops");

        var (status, took) = await TimeAsync(() => client.GetAsync("/ws/hops"));
        var reporter = From(host).Internal();
        var (_, reportTook) = await TimeAsync(() => reporter.PostAsJsonAsync("/hops", new BatchTracker.Application.Hop("t", "b", "farm-a", "api", "Created", DateTimeOffset.UtcNow, true), JsonDefaults.Options));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.True(took >= Step - Timing.Tick, $"{took}");
        Assert.True(reportTook < Step, $"{reportTook}");
        Assert.Equal(3, LedgerOf(host).FailuresOf("203.0.113.20"));
    }

    [Fact]
    public async Task REQ_BT_049_Line_delays_its_401s()
    {
        await using var line = new LineHost(Stack.Api);
        var client = From(Delayed(line));
        await SpendFreeFailuresAsync(client, "/line");

        var (status, took) = await TimeAsync(() => client.GetAsync("/line"));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.True(took >= Step - Timing.Tick, $"{took}");
    }
}
