using System.Net;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BatchTracker.IntegrationTests;

/// <summary>Real hosts of each service, with only the wire to Live captured.</summary>
public class HopPodTests(Stack stack) : StackTest(stack)
{
    private const string Path = "/ws/hops";

    /// <summary>Stands in for Live and keeps the first body it is sent.</summary>
    private sealed class Capture : HttpMessageHandler
    {
        public TaskCompletionSource<string> Body { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            if (body.Contains("\"hop\"")) Body.TrySetResult(body);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    /// <summary>A hop with no pod, as every service builds it.</summary>
    private static Hop NewHop() => new("trace", "batch", "farm-a", HopNames.Worker, "Created", DateTimeOffset.UtcNow, true);

    /// <summary>Reports through the host's own registered reporter and returns what went on the wire.</summary>
    private static async Task<JsonElement> ReportedByAsync<T>(WebApplicationFactory<T> host, string? pod) where T : class
    {
        var capture = new Capture();
        await using var wired = host.WithWebHostBuilder(b =>
        {
            if (pod is not null) b.UseSetting("POD_NAME", pod);
            b.ConfigureTestServices(s => s.AddHttpClient<ReportFlusher>().ConfigurePrimaryHttpMessageHandler(() => capture));
        });

        wired.Services.GetRequiredService<IHopReporter>().Report(NewHop());

        var body = await capture.Body.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return JsonDocument.Parse(body).RootElement.GetProperty("hops")[0].Clone();
    }

    [Fact]
    public async Task REQ_BT_028_A_Worker_hop_names_the_pod_that_handled_it()
    {
        await using var worker = new WorkerHost();

        var hop = await ReportedByAsync(worker, "worker-7");

        Assert.Equal("worker-7", hop.GetProperty("pod").GetString());
    }

    [Fact]
    public async Task REQ_BT_028_A_Dispatcher_hop_names_the_pod_that_handled_it()
    {
        await using var dispatcher = new DispatcherHost(_ => { });

        var hop = await ReportedByAsync(dispatcher, "dispatcher-3");

        Assert.Equal("dispatcher-3", hop.GetProperty("pod").GetString());
    }

    [Fact]
    public async Task REQ_BT_028_A_Worker_outside_a_pod_uses_the_machine_name()
    {
        await using var worker = new WorkerHost();

        var hop = await ReportedByAsync(worker, null);

        Assert.Equal(Environment.MachineName, hop.GetProperty("pod").GetString());
    }

    [Fact]
    public async Task REQ_BT_028_An_Api_hop_stays_as_it_was()
    {
        await using var worker = new WorkerHost();
        await using var api = new ApiHost(worker);

        var hop = await ReportedByAsync(api, "api-1");

        Assert.False(hop.TryGetProperty("pod", out _));
    }

    [Fact]
    public async Task REQ_BT_028_The_pod_reaches_clients_in_frames_and_in_history()
    {
        await using var live = new LiveHost();
        var reporter = new ReportBuffer(source: new HopSource("worker-7"));
        using var socket = await Sockets.OpenAsync(live.Server, Path, Sockets.CookieFor("ada"));
        await Sockets.ReceiveTypeAsync(socket, "snapshot");

        reporter.Report(NewHop());
        await new ReportFlusher(live.CreateClient().Internal(), reporter).FlushAsync(default);

        var frame = await Sockets.ReceiveTypeAsync(socket, "hops");
        Assert.Equal("worker-7", frame.GetProperty("hops")[0].GetProperty("pod").GetString());

        using var later = await Sockets.OpenAsync(live.Server, Path, Sockets.CookieFor("ada"));
        var snapshot = await Sockets.ReceiveTypeAsync(later, "snapshot");
        Assert.Equal("worker-7", snapshot.GetProperty("hops")[0].GetProperty("pod").GetString());
    }
}
