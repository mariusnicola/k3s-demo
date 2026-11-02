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

public class LineStopTests(Stack stack) : StackTest(stack)
{
    /// <summary>Holds every create until released, so a walk is in flight when the stop arrives.</summary>
    private sealed class HoldCreate(HttpMessageHandler inner, TaskCompletionSource entered, TaskCompletionSource release) : DelegatingHandler(inner)
    {
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.EndsWith("/batches")) return await base.SendAsync(request, token);
            Interlocked.Increment(ref Calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }
    }

    private static LineOptions Options(TimeSpan wait) => new() { StopWait = wait };

    [Fact]
    public async Task REQ_BT_050_Stop_lets_the_walk_in_flight_finish_and_starts_no_new_one()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HoldCreate? hold = null;
        await using var line = new LineHost(Stack.Api, Options(TimeSpan.FromSeconds(10)), inner => hold = new HoldCreate(inner, entered, release));
        var engine = line.Services.GetRequiredService<LineEngine>();
        var log = line.Services.GetRequiredService<LineLog>();
        engine.Start(1);
        await entered.Task;

        var stopping = engine.StopAsync(default);
        await Task.Delay(1500);
        var calls = hold!.Calls;
        Assert.False(stopping.IsCompleted);

        release.SetResult();
        await stopping;

        Assert.Equal(1, calls);
        Assert.Equal(1, log.Counts().Errors);
    }

    [Fact]
    public async Task REQ_BT_050_Walk_past_the_wait_is_cancelled_with_no_entry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var line = new LineHost(
            Stack.Api, Options(TimeSpan.FromMilliseconds(200)), inner => new HoldCreate(inner, entered, new TaskCompletionSource()));
        var engine = line.Services.GetRequiredService<LineEngine>();
        var log = line.Services.GetRequiredService<LineLog>();
        engine.Start(1);
        await entered.Task;
        var clock = Stopwatch.StartNew();

        await engine.StopAsync(default);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{clock.Elapsed}");
        Assert.Equal(0, log.Counts().Errors);
    }
}
