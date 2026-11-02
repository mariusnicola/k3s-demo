using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using BatchTracker.Dispatcher;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using k8s.LeaderElection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BatchTracker.IntegrationTests;

/// <summary>A real listener, so the order of readiness, draining and closing is the one a pod sees.</summary>
public class GracefulStopTests
{
    private static async Task<(WebApplication App, HttpClient Client, TaskCompletionSource Release, TaskCompletionSource Entered)> StartAsync(ShutdownRules rules)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddGracefulShutdown();
        builder.Services.RemoveAll<ShutdownRules>().AddSingleton(rules);
        var app = builder.Build();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapReady();
        app.MapGet("/quick", () => Results.Ok());
        app.MapGet("/slow", async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return Results.Ok();
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return (app, new HttpClient { BaseAddress = new Uri(address) }, release, entered);
    }

    [Fact]
    public async Task REQ_BT_050_Readiness_fails_while_the_listener_still_serves_and_work_in_flight_finishes()
    {
        var (app, client, release, entered) = await StartAsync(new ShutdownRules { Drain = TimeSpan.FromMilliseconds(800), Finish = TimeSpan.FromSeconds(10) });
        var slow = client.GetAsync("/slow");
        await entered.Task;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).StatusCode);

        var stopping = app.StopAsync();
        await Task.Delay(100);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/quick")).StatusCode);
        Assert.False(stopping.IsCompleted);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(stopping.IsCompleted);

        release.SetResult();

        Assert.Equal(HttpStatusCode.OK, (await slow).StatusCode);
        await stopping;
    }

    [Fact]
    public async Task REQ_BT_050_Work_past_the_limit_does_not_hold_the_exit()
    {
        var (app, client, _, entered) = await StartAsync(new ShutdownRules { Drain = TimeSpan.Zero, Finish = TimeSpan.FromMilliseconds(500) });
        var slow = client.GetAsync("/slow");
        await entered.Task;
        var clock = Stopwatch.StartNew();

        try
        {
            await app.StopAsync();
        }
        catch (Exception e) when (e is OperationCanceledException or AggregateException)
        {
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{clock.Elapsed}");
        await Assert.ThrowsAnyAsync<Exception>(() => slow);
    }

    [Fact]
    public async Task REQ_BT_050_Second_stop_signal_does_not_crash()
    {
        var (app, _, _, _) = await StartAsync(new ShutdownRules { Drain = TimeSpan.FromMilliseconds(50), Finish = TimeSpan.FromSeconds(1) });

        await Task.WhenAll(app.StopAsync(), app.StopAsync());
        await app.StopAsync();
    }

    [Fact]
    public void REQ_BT_050_Limits_add_up_inside_the_default_grace()
    {
        var rules = new ShutdownRules();

        Assert.Equal(TimeSpan.FromSeconds(5), rules.Drain);
        Assert.Equal(TimeSpan.FromSeconds(20), rules.Finish);
        Assert.True(rules.Drain + rules.Finish < TimeSpan.FromSeconds(30));
    }
}
