using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BatchTracker.Application.Dispatching;
using BatchTracker.Dispatcher;
using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace BatchTracker.IntegrationTests;

/// <summary>Runs the real LeaderElector. Only the Kubernetes API behind it is faked, because tests may not touch the cluster.</summary>
public sealed class LeaderElectionTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private sealed class OutputLogger(ITestOutputHelper output) : ILogger<KubernetesElection>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                output.WriteLine($"{formatter(state, exception)}: {exception?.GetType().Name} {exception?.Message}");
            }
            catch (InvalidOperationException)
            {
                // The election can outlive the test that owns this output.
            }
        }
    }

    private (KubernetesElection Election, LeadershipSignal Signal, FlakyLink Link) NewPod(LeaseApi api, string name)
    {
        var signal = new LeadershipSignal();
        var link = new FlakyLink(api);
        var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://kubernetes.test" }, link);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["POD_NAME"] = name })
            .Build();
        return (new KubernetesElection(signal, client, configuration, new OutputLogger(output)), signal, link);
    }

    [Fact]
    public async Task REQ_BT_018_Pod_gains_the_lease_named_dispatcher_in_batch_tracker()
    {
        var api = new LeaseApi();
        var pod = NewPod(api, "pod-a");

        await pod.Election.StartAsync(default);
        await pod.Signal.WaitForLeadershipAsync(default).WaitAsync(Limit);
        var holder = api.Holder;
        var duration = api.DurationSeconds;
        await pod.Election.StopAsync(default);

        Assert.Equal("/apis/coordination.k8s.io/v1/namespaces/batch-tracker/leases/dispatcher", api.LeasePath);
        Assert.Equal("pod-a", holder);
        Assert.Equal(10, duration);
    }

    [Fact]
    public async Task REQ_BT_050_Stopping_leader_hands_over_well_inside_the_lease()
    {
        var api = new LeaseApi();
        var first = NewPod(api, "pod-a");
        var second = NewPod(api, "pod-b");
        await first.Election.StartAsync(default);
        await first.Signal.WaitForLeadershipAsync(default).WaitAsync(Limit);
        await second.Election.StartAsync(default);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await first.Election.StopAsync(default);
        await second.Signal.WaitForLeadershipAsync(default).WaitAsync(Limit);

        var handover = clock.Elapsed;
        await second.Election.StopAsync(default);
        Assert.Equal("", api.Holder);
        Assert.True(handover < TimeSpan.FromSeconds(6), $"{handover}");
    }

    [Fact]
    public async Task REQ_BT_018_Two_pods_exactly_one_leads()
    {
        var api = new LeaseApi();
        var first = NewPod(api, "pod-a");
        var second = NewPod(api, "pod-b");

        await first.Election.StartAsync(default);
        await first.Signal.WaitForLeadershipAsync(default).WaitAsync(Limit);
        await second.Election.StartAsync(default);
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.False(second.Signal.WaitForLeadershipAsync(default).IsCompleted);
        Assert.Equal("pod-a", api.Holder);

        await first.Election.StopAsync(default);
        await second.Election.StopAsync(default);
    }

    [Fact]
    public async Task REQ_BT_019_Failed_renewals_end_the_term_within_the_deadline_and_the_other_pod_takes_over()
    {
        var api = new LeaseApi();
        var first = NewPod(api, "pod-a");
        var second = NewPod(api, "pod-b");
        await first.Election.StartAsync(default);
        var term = await first.Signal.WaitForLeadershipAsync(default).WaitAsync(Limit);
        await second.Election.StartAsync(default);

        first.Link.Broken = true;
        var cut = DateTimeOffset.UtcNow;

        var ended = new TaskCompletionSource();
        term.Register(() => ended.TrySetResult());
        await ended.Task.WaitAsync(Limit);
        var stepDown = DateTimeOffset.UtcNow - cut;

        await second.Signal.WaitForLeadershipAsync(default).WaitAsync(Limit);
        var takeover = DateTimeOffset.UtcNow - cut;

        // Must stop before the 10 s lease can pass to another pod.
        Assert.True(stepDown < TimeSpan.FromSeconds(10), $"stepped down after {stepDown.TotalSeconds:F1} s");
        Assert.True(takeover > stepDown, "the old leader must stop before the new one starts");
        Assert.Equal("pod-b", api.Holder);

        await first.Election.StopAsync(default);
        await second.Election.StopAsync(default);
    }

    /// <summary>
    /// The wait is taken first, as the runner takes it. A connection failure is not an API error: the library lets it
    /// out of its acquire loop, which is what ended the election here and left a live pod renewing with no leader running.
    /// </summary>
    [Fact]
    public async Task REQ_BT_019_A_standby_cut_off_from_the_api_still_leads_when_the_lease_comes_free()
    {
        var api = new LeaseApi();
        var first = NewPod(api, "pod-a");
        var second = NewPod(api, "pod-b");
        await first.Election.StartAsync(default);
        await first.Signal.WaitForLeadershipAsync(default).WaitAsync(Limit);
        var waiting = second.Signal.WaitForLeadershipAsync(default);
        await second.Election.StartAsync(default);
        await Task.Delay(TimeSpan.FromSeconds(1));

        second.Link.Unreachable = true;
        await Task.Delay(TimeSpan.FromSeconds(4));
        second.Link.Unreachable = false;
        await first.Election.StopAsync(default);

        var term = await waiting.WaitAsync(Limit);
        Assert.False(term.IsCancellationRequested);
        Assert.Equal("pod-b", api.Holder);
        await second.Election.StopAsync(default);
    }

    private sealed class FlakyLink(LeaseApi api) : DelegatingHandler
    {
        public volatile bool Broken;
        public volatile bool Unreachable;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Unreachable) throw new HttpRequestException("An error occurred while sending the request.");
            var response = Broken
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }
                : await api.HandleAsync(request, token);

            // The client builds its error from the request carried on the response.
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class LeaseApi
    {
        private readonly object sync = new();
        private JsonNode? lease;
        private int version;

        public string? LeasePath { get; private set; }

        public string? Holder
        {
            get { lock (sync) return lease?["spec"]?["holderIdentity"]?.GetValue<string>(); }
        }

        public int? DurationSeconds
        {
            get { lock (sync) return lease?["spec"]?["leaseDurationSeconds"]?.GetValue<int>(); }
        }

        public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(token));
            lock (sync)
            {
                if (request.Method == HttpMethod.Get)
                {
                    LeasePath = request.RequestUri!.AbsolutePath;
                    return lease is null ? Status(HttpStatusCode.NotFound) : Json(lease);
                }
                if (request.Method == HttpMethod.Post)
                {
                    LeasePath = request.RequestUri!.AbsolutePath + "/dispatcher";
                    return lease is null ? Store(body!) : Status(HttpStatusCode.Conflict);
                }
                var sent = body!["metadata"]?["resourceVersion"]?.GetValue<string>();
                return sent == version.ToString() ? Store(body) : Status(HttpStatusCode.Conflict);
            }
        }

        private HttpResponseMessage Store(JsonNode body)
        {
            version++;
            body["metadata"]!["resourceVersion"] = version.ToString();
            lease = body;
            return Json(body);
        }

        private static HttpResponseMessage Json(JsonNode body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        private static HttpResponseMessage Status(HttpStatusCode code) => new(code)
        {
            Content = new StringContent(
                $$"""{"kind":"Status","apiVersion":"v1","status":"Failure","code":{{(int)code}}}""",
                Encoding.UTF8, "application/json")
        };
    }
}
