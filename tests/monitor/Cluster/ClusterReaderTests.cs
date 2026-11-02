using Xunit;
using static FakeCluster;

public sealed class ClusterReaderTests : IDisposable
{
    private const string App = "batch-tracker";
    private const string Nodes = "/api/v1/nodes";
    private const string Pods = "/api/v1/pods";
    private const string Namespaces = "/api/v1/namespaces";
    private const string PodMetrics = "/apis/metrics.k8s.io/v1beta1/pods";
    private const string NodeMetrics = "/apis/metrics.k8s.io/v1beta1/nodes";
    private const string Lease = "/apis/coordination.k8s.io/v1/namespaces/batch-tracker/leases/dispatcher";
    private const string Config = "/api/v1/namespaces/batch-tracker/configmaps/batch-tracker-config";

    private const string Caps = """
        {"nodes":{"w1":{"cpuMilli":1000,"memoryMi":1000}},"reserved":[],
         "pods":[{"name":"api","node":"w1","cpuMilli":500,"memoryMi":256,"namespace":"batch-tracker","pod":"^api","container":"c","replicas":2}]}
        """;

    private readonly FakeCluster cluster = new();
    private readonly string capsPath = Path.GetTempFileName();

    public ClusterReaderTests()
    {
        File.WriteAllText(capsPath, Caps);
        cluster.Answer(Nodes, List(Node("w1")));
        cluster.Answer(Pods, List(Pod(App, "api-1")));
        cluster.Answer(Namespaces, List("""{"metadata":{"name":"batch-tracker"}}"""));
    }

    public void Dispose() => File.Delete(capsPath);

    private ClusterReader Reader(FakeCluster answers, string[] namespaces, TimeSpan? timeout = null) =>
        new(answers.Client(), "control", App, namespaces, timeout ?? TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500), capsPath, _ => Task.FromResult(new Dictionary<string, bool?>()));

    private void UsageAnswers()
    {
        cluster.Answer(PodMetrics, List("""{"metadata":{"namespace":"batch-tracker","name":"api-1"},"containers":[{"name":"c","usage":{"cpu":"100m","memory":"64Mi"}}]}"""));
        cluster.Answer(NodeMetrics, List("""{"metadata":{"name":"w1"},"usage":{"cpu":"250m","memory":"512Mi"}}"""));
    }

    [Fact]
    public async Task A_full_read_gives_the_control_plane_the_workers_the_pods_and_the_caps()
    {
        UsageAnswers();

        var snapshot = await Reader(cluster, []).Collect();

        Assert.Null(snapshot.Error);
        Assert.True(snapshot.ApiAnswered);
        Assert.True(snapshot.AppInstalled);
        Assert.Equal(2, snapshot.Nodes.Length);
        Assert.Equal(("control", "control plane", true, (int?)null), (snapshot.Nodes[0].Name, snapshot.Nodes[0].Role, snapshot.Nodes[0].Ready, snapshot.Nodes[0].AllocatableMemoryMi));
        Assert.Equal(new NodeRow("w1", "worker", true, 2048, 250, 512), snapshot.Nodes[1]);
        Assert.Equal([new PodRow(App, "api-1", "w1", "Running", true, 500, 256, 100, 64)], snapshot.Pods);
        Assert.Equal(new CapRow("w1", "cpuMilli", 1000, 1000, false), snapshot.Caps[0]);
        Assert.Empty(snapshot.CapIssues);
    }

    [Fact]
    public async Task A_pod_outside_the_checked_namespaces_or_ended_stays_out()
    {
        var cluster = new FakeCluster();
        cluster.Answer(Nodes, List(Node("w1")));
        cluster.Answer(Namespaces, List());
        cluster.Answer(Pods, List(
            Pod(App, "api-1"), Pod("elsewhere", "other"), Pod(App, "done", "Succeeded"), Pod("kube-system", "dns")));

        var snapshot = await Reader(cluster, ["kube-system"]).Collect();

        Assert.Equal([$"{App}/api-1", "kube-system/dns"], snapshot.Pods.Select(p => $"{p.Namespace}/{p.Name}"));
        Assert.Equal(["kube-system/dns/c not in caps.json"], snapshot.CapIssues);
    }

    [Fact]
    public async Task A_terminating_pod_shows_with_its_limits_and_counts_against_no_entry()
    {
        var cluster = new FakeCluster();
        cluster.Answer(Nodes, List(Node("w1")));
        cluster.Answer(Namespaces, List());
        cluster.Answer(Pods, List(Pod(App, "api-1"), Pod(App, "api-2"), Pod(App, "api-old", deleted: true)));

        var snapshot = await Reader(cluster, []).Collect();

        Assert.Equal(new PodRow(App, "api-old", "w1", "Running", true, 500, 256, null, null, Terminating: true), snapshot.Pods[2]);
        Assert.False(snapshot.Pods[0].Terminating);
        Assert.Equal(new CapRow("w1", "cpuMilli", 1000, 1000, false), snapshot.Caps[0]);
        Assert.Empty(snapshot.CapIssues);
    }

    [Fact]
    public async Task A_pod_that_failed_shows_but_holds_no_limit_and_counts_against_no_entry()
    {
        var cluster = new FakeCluster();
        cluster.Answer(Nodes, List(Node("w1")));
        cluster.Answer(Namespaces, List());
        cluster.Answer(Pods, List(Pod(App, "stray", "Failed")));

        var snapshot = await Reader(cluster, []).Collect();

        Assert.Equal([new PodRow(App, "stray", "w1", "Failed", true, 0, 0, null, null)], snapshot.Pods);
        Assert.Empty(snapshot.CapIssues);
    }

    [Fact]
    public async Task With_no_app_namespace_the_app_is_not_installed()
    {
        var cluster = new FakeCluster();
        cluster.Answer(Nodes, List(Node("w1", "False")));
        cluster.Answer(Pods, List());
        cluster.Answer(Namespaces, List("""{"metadata":{"name":"kube-system"}}"""));

        var snapshot = await Reader(cluster, []).Collect();

        Assert.False(snapshot.AppInstalled);
        Assert.False(snapshot.Nodes[1].Ready);
    }

    [Fact]
    public async Task The_snapshot_names_the_dispatcher_that_holds_the_lease()
    {
        cluster.Answer(Lease, $$$"""{"metadata":{"name":"dispatcher"},"spec":{"holderIdentity":"dispatcher-a","leaseDurationSeconds":10,"renewTime":"{{{DateTime.UtcNow:O}}}"}}""");

        var snapshot = await Reader(cluster, []).Collect();

        Assert.Equal("dispatcher-a", snapshot.DispatcherLeader);
    }

    [Fact]
    public async Task A_lease_that_cannot_be_read_names_nobody_and_keeps_the_report()
    {
        cluster.Fail(Lease);

        var snapshot = await Reader(cluster, []).Collect();

        Assert.Null(snapshot.Error);
        Assert.Null(snapshot.DispatcherLeader);
        Assert.Single(snapshot.Pods);
    }

    [Fact]
    public async Task A_stuck_lease_read_ends_at_the_usage_limit()
    {
        cluster.Hang(Lease);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = await Reader(cluster, []).Collect();

        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 10);
        Assert.Null(snapshot.Error);
        Assert.Null(snapshot.DispatcherLeader);
    }

    [Fact]
    public async Task The_snapshot_names_the_store_the_api_runs()
    {
        cluster.Answer(Config, """{"metadata":{"name":"batch-tracker-config"},"data":{"BATCH_STORE":"mongo"}}""");

        var snapshot = await Reader(cluster, []).Collect();

        Assert.Equal("mongo", snapshot.Store);
    }

    [Fact]
    public async Task A_config_that_cannot_be_read_names_no_store_and_keeps_the_report()
    {
        cluster.Fail(Config);

        var snapshot = await Reader(cluster, []).Collect();

        Assert.Null(snapshot.Error);
        Assert.Null(snapshot.Store);
        Assert.Single(snapshot.Pods);
    }

    [Fact]
    public async Task A_config_with_no_store_key_names_no_store()
    {
        cluster.Answer(Config, """{"metadata":{"name":"batch-tracker-config"},"data":{}}""");

        var snapshot = await Reader(cluster, []).Collect();

        Assert.Null(snapshot.Store);
    }

    [Fact]
    public async Task A_stuck_config_read_ends_at_the_usage_limit()
    {
        cluster.Hang(Config);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = await Reader(cluster, []).Collect();

        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 10);
        Assert.Null(snapshot.Store);
    }

    [Fact]
    public void The_report_sends_the_leader_and_the_terminating_mark_by_these_names()
    {
        var report = new Report(DateTimeOffset.UnixEpoch, true, [], [], [new PodRow(App, "api-1", "w1", "Running", true, 0, 0, null, null, Terminating: true)], [], [], "dispatcher-a", "mongo");

        var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"dispatcherLeader\":\"dispatcher-a\"", json);
        Assert.Contains("\"terminating\":true", json);
        Assert.Contains("\"store\":\"mongo\"", json);
    }

    [Fact]
    public async Task A_metrics_outage_keeps_the_rows_with_no_usage()
    {
        cluster.Fail(PodMetrics);
        cluster.Hang(NodeMetrics);

        var snapshot = await Reader(cluster, []).Collect();

        Assert.Null(snapshot.Error);
        Assert.Equal(new NodeRow("w1", "worker", true, 2048, null, null), snapshot.Nodes[1]);
        Assert.Equal([new PodRow(App, "api-1", "w1", "Running", true, 500, 256, null, null)], snapshot.Pods);
    }

    [Fact]
    public async Task With_the_api_down_the_snapshot_says_so_and_the_control_plane_is_not_ready()
    {
        var cluster = new FakeCluster();
        cluster.Fail(Nodes);

        var snapshot = await Reader(cluster, []).Collect();

        Assert.NotNull(snapshot.Error);
        Assert.False(snapshot.ApiAnswered);
        Assert.False(snapshot.AppInstalled);
        Assert.False(Assert.Single(snapshot.Nodes).Ready);
        Assert.Empty(snapshot.Pods);
    }

    [Fact]
    public async Task A_read_that_fails_after_the_api_answered_keeps_the_control_plane_ready()
    {
        var cluster = new FakeCluster();
        cluster.Answer(Nodes, List(Node("w1")));
        cluster.Fail(Pods);

        var snapshot = await Reader(cluster, []).Collect();

        Assert.NotNull(snapshot.Error);
        Assert.True(snapshot.ApiAnswered);
        Assert.True(Assert.Single(snapshot.Nodes).Ready);
    }

    [Fact]
    public async Task A_stuck_read_ends_at_the_time_limit_as_an_error()
    {
        var cluster = new FakeCluster();
        cluster.Hang(Nodes);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = await Reader(cluster, [], TimeSpan.FromMilliseconds(100)).Collect();

        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 10);
        Assert.NotNull(snapshot.Error);
        Assert.False(snapshot.ApiAnswered);
    }

    [Fact]
    public async Task A_caps_file_that_does_not_load_is_an_error_after_the_api_answered()
    {
        File.Delete(capsPath);

        var snapshot = await Reader(cluster, []).Collect();

        Assert.NotNull(snapshot.Error);
        Assert.True(snapshot.ApiAnswered);
    }
}
