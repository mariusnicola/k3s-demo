using System.Diagnostics;
using System.Net;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BatchTracker.IntegrationTests;

/// <summary>Builds each client the way its service registers it, so the real handler chain runs over real time.</summary>
public class RetryWiringTests
{
    private sealed class Script(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(answer(Interlocked.Increment(ref calls)));
    }

    private static readonly IConfiguration Settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["BATCH_STORE"] = "mongo",
        ["WORKER_URL"] = "http://worker/",
        ["INTERNAL_TOKEN"] = "token",
        ["LIVE_URL"] = "http://live/"
    }).Build();

    private static ServiceProvider Wired(Script script, Action<IServiceCollection> add)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        add(services);
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new Forward(script)));
        return services.BuildServiceProvider();
    }

    /// <summary>A report only leaves through the flush loop, and that loop is a hosted service nothing else starts here.</summary>
    private static async Task<ServiceProvider> FlushingAsync(ServiceProvider provider)
    {
        foreach (var service in provider.GetServices<IHostedService>()) await service.StartAsync(default);
        return provider;
    }

    private sealed class Forward(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
            // The script outlives handler rotation, so nothing here owns it.
        }
    }

    private static IWorkerClient Worker(Script script) =>
        Wired(script, s => s.AddWorkerClient(Settings)).GetRequiredService<IWorkerClient>();

    private static BatchEvent AnEvent() =>
        new("event", "batch", "farm-a", "trace", "Dose1");

    private static HttpResponseMessage Fail(int call, int failures, HttpStatusCode then = HttpStatusCode.OK) =>
        new(call <= failures ? HttpStatusCode.ServiceUnavailable : then);

    private static async Task UntilAsync(Func<bool> done)
    {
        var start = Stopwatch.StartNew();
        while (!done() && start.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
    }

    [Fact]
    public async Task REQ_BT_050_Event_delivery_survives_two_503s()
    {
        var script = new Script(call => Fail(call, 2));

        var result = await Worker(script).PostEventAsync(AnEvent(), default);

        Assert.Equal(WorkerResult.Done, result);
        Assert.Equal(3, script.Calls);
    }

    [Fact]
    public async Task REQ_BT_050_Worker_422_is_not_retried()
    {
        var script = new Script(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));

        var result = await Worker(script).PostEventAsync(AnEvent(), default);

        Assert.Equal(WorkerResult.Refused, result);
        Assert.Equal(1, script.Calls);
    }

    [Fact]
    public async Task REQ_BT_050_Readiness_probe_answers_on_its_first_attempt()
    {
        var script = new Script(call => Fail(call, 1));

        Assert.False(await Worker(script).ReadyAsync(default));
        Assert.Equal(1, script.Calls);
    }

    [Fact]
    public async Task REQ_BT_050_429_with_retry_after_makes_the_caller_wait_at_least_that_long()
    {
        var script = new Script(call =>
        {
            if (call > 1) return new HttpResponseMessage(HttpStatusCode.OK);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "1");
            return response;
        });
        var clock = Stopwatch.StartNew();

        await Worker(script).PostEventAsync(AnEvent(), default);

        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1) - Timing.Tick, $"{clock.Elapsed}");
    }

    [Fact]
    public async Task REQ_BT_050_Service_down_for_all_ten_attempts_fails_after_40_s_and_no_hang()
    {
        var script = new Script(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var clock = Stopwatch.StartNew();

        var result = await Worker(script).PostEventAsync(AnEvent(), default);

        Assert.Equal(WorkerResult.Unavailable, result);
        Assert.Equal(10, script.Calls);
        Assert.InRange(clock.Elapsed.TotalSeconds, 40.5, 85);
    }

    [Fact]
    public async Task REQ_BT_021_A_contended_batch_comes_back_as_contended_on_the_first_answer()
    {
        var script = new Script(_ => new HttpResponseMessage(HttpStatusCode.Conflict));

        var result = await Worker(script).PostEventAsync(AnEvent(), default);

        Assert.Equal(WorkerResult.Contended, result);
        Assert.Equal(1, script.Calls);
    }

    [Fact]
    public async Task REQ_BT_050_Hop_report_survives_two_503s_and_never_holds_the_caller()
    {
        var script = new Script(call => Fail(call, 2, HttpStatusCode.Accepted));
        await using var provider = await FlushingAsync(Wired(script, s => s.AddHopReporter(Settings)));
        var hops = provider.GetRequiredService<IHopReporter>();
        var clock = Stopwatch.StartNew();

        hops.Report(new Hop("trace", "batch", "farm-a", "api", "Created", DateTimeOffset.UtcNow, true));

        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(100), $"{clock.Elapsed}");
        await UntilAsync(() => script.Calls == 3);
        Assert.Equal(3, script.Calls);
    }

    [Fact]
    public async Task REQ_BT_050_Change_report_survives_two_503s()
    {
        var script = new Script(call => Fail(call, 2, HttpStatusCode.Accepted));
        await using var provider = await FlushingAsync(Wired(script, s => s.AddBatchStore(Settings)));
        var changes = provider.GetRequiredService<IChangeReporter>();

        changes.Report(new Versioned(BatchMachine.Create("id", "farm-a", "p", new Stamp("u", "t", DateTimeOffset.UtcNow)), "etag"));

        await UntilAsync(() => script.Calls == 3);
        Assert.Equal(3, script.Calls);
    }
}
