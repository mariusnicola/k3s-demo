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

public class ServiceStopTests(Stack stack) : StackTest(stack)
{
    private static void Stopping<T>(WebApplicationFactory<T> host) where T : class =>
        host.Services.GetRequiredService<ShutdownState>().Begin();

    private static TimeSpan Limit<T>(WebApplicationFactory<T> host) where T : class =>
        host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;

    [Fact]
    public async Task REQ_BT_050_Api_readiness_fails_on_stop_and_liveness_holds()
    {
        await using var api = new ApiHost(Stack.Worker);
        var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/ready")).StatusCode);

        Stopping(api);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/healthz")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_050_Live_readiness_fails_on_stop_and_liveness_holds()
    {
        await using var live = new LiveHost();
        var client = live.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).StatusCode);

        Stopping(live);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_050_Line_readiness_fails_on_stop_and_liveness_holds()
    {
        await using var line = new LineHost(Stack.Api);
        var client = line.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).StatusCode);

        Stopping(line);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_050_Every_service_configures_the_shutdown_timeout()
    {
        await using var api = new ApiHost(Stack.Worker);
        await using var worker = new WorkerHost();
        await using var live = new LiveHost();
        await using var line = new LineHost(Stack.Api);
        await using var dispatcher = new DispatcherHost(_ => { });
        var limit = new ShutdownRules { Drain = TimeSpan.Zero }.Drain + new ShutdownRules().Finish;

        Assert.Equal(limit, Limit(api));
        Assert.Equal(limit, Limit(dispatcher));
        Assert.Equal(limit, Limit(worker));
        Assert.Equal(limit, Limit(live));
        Assert.Equal(limit, Limit(line));
    }

    [Fact]
    public async Task REQ_BT_050_Open_socket_is_closed_cleanly_on_stop()
    {
        await using var live = new LiveHost();
        var socket = await Sockets.OpenAsync(live.Server, "/ws/hops", Sockets.CookieFor("ada"));

        await Sockets.ReceiveTypeAsync(socket, "snapshot");
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receiving = socket.ReceiveAsync(new byte[64 * 1024], limit.Token);

        live.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        var result = await receiving;
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, result.CloseStatus);
    }

    [Fact]
    public async Task REQ_BT_050_New_socket_during_drain_is_refused_with_503_and_retry_after()
    {
        await using var live = new LiveHost();
        Stopping(live);

        var request = new HttpRequestMessage(HttpMethod.Get, "/ws/hops");
        request.Headers.Add("Cookie", Sockets.CookieFor("ada"));
        request.Headers.Add("Origin", Env.Origin);
        var reply = await live.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.StatusCode);
        Assert.True(reply.Headers.Contains("Retry-After"));
    }
}
