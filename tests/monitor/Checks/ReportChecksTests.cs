using Xunit;
using static FakeCluster;

public class ReportChecksTests
{
    private const string App = "batch-tracker";
    private const string System = "kube-system";
    /// <summary>Nothing listens on this address, so the service and page checks never reach a real one.</summary>
    private const string Nowhere = "127.0.0.2";
    private const string Origin = $"http://{Nowhere}:1";
    private const string History = """{"status":"success","data":{"result":[{"value":[0,"1000"]}]}}""";

    private readonly FakeCluster cluster = new();

    private static string Deployment(string name, int replicas, int ready) =>
        """{"metadata":{"name":"@name"},"spec":{"replicas":@replicas,"selector":{},"template":{}},"status":{"readyReplicas":@ready}}"""
            .Replace("@name", name).Replace("@replicas", replicas.ToString()).Replace("@ready", ready.ToString());

    private static string DaemonSet(string name, int desired, int ready) =>
        """{"metadata":{"name":"@name"},"spec":{"selector":{},"template":{}},"status":{"desiredNumberScheduled":@desired,"numberReady":@ready,"currentNumberScheduled":0,"numberMisscheduled":0}}"""
            .Replace("@name", name).Replace("@desired", desired.ToString()).Replace("@ready", ready.ToString());

    private static Snapshot Reading(bool app = false, bool answered = true, string? error = null, CapRow[]? caps = null, string[]? issues = null, string? leader = null, string? store = null) =>
        new([], [], caps ?? [], issues ?? [], app, answered, error, leader, store);

    private void Healthy(string space)
    {
        cluster.Answer($"/apis/apps/v1/namespaces/{space}/deployments", List(Deployment("d", 2, 2)));
        cluster.Answer($"/apis/apps/v1/namespaces/{space}/daemonsets", List(DaemonSet("ds", 1, 1)));
        cluster.Answer($"/apis/autoscaling/v2/namespaces/{space}/horizontalpodautoscalers", List());
    }

    private void HealthyCluster()
    {
        cluster.Answer("/api/v1/nodes/w1", Node("w1"));
        Healthy(System);
        cluster.Answer("/api/v1/namespaces/ingress-nginx/services/ingress-nginx-controller",
            """{"metadata":{"name":"ingress-nginx-controller"},"status":{"loadBalancer":{"ingress":[{"ip":"198.18.2.1"}]}}}""");
    }

    private void HealthyApp()
    {
        Healthy(App);
        cluster.Answer($"/apis/batch/v1/namespaces/{App}/cronjobs/nightly", """{"metadata":{"name":"nightly"},"spec":{"schedule":"0 0 * * *","jobTemplate":{}}}""");
        cluster.Answer($"/apis/batch/v1/namespaces/{App}/jobs", List());
        cluster.Answer($"/api/v1/namespaces/{App}/services/http:api:8080/proxy/api/ready/deep", "ok");
    }

    private Task<Check[]> Run(Snapshot snapshot, NetCheck[]? network = null, string? prometheusFails = null)
    {
        var metrics = new MetricSeries((_, _) => prometheusFails is null ? Task.FromResult(History) : throw new InvalidOperationException(prometheusFails));
        var checks = new ReportChecks(cluster.Client(), new MachineChecks(Origin, "token", TimeProvider.System), metrics, Nowhere, MongoNowhere, Origin,
            ["w1"], [System], App, ["/"], TimeSpan.FromMilliseconds(500));
        return Task.WhenAll(checks.All(snapshot, network ?? []));
    }

    private static string MongoNowhere => $"mongodb://{Nowhere}:27018/?serverSelectionTimeoutMS=3000&retryWrites=false";

    private static Check Named(Check[] checks, string name) => checks.Single(c => c.Name == name);

    [Fact]
    public async Task With_the_namespace_gone_the_app_checks_stay_and_fail()
    {
        HealthyCluster();

        var checks = await Run(Reading());

        string[] cluster = ["control plane", "node w1", $"workloads {System}", "ingress address", "service bus", "cosmos", "mongo", "caps", "prometheus"];
        string[] app = [$"workloads {App}", "nightly job", "app api ready", "dispatcher sweep", "page /", "ingress /ws", "ingress /line", "line bundles", "ingress /ws/hops"];
        Assert.Equal([.. cluster, .. app], checks.Select(c => c.Name));
        Assert.All(checks.Where(c => c.Name is not ("service bus" or "cosmos" or "mongo") && cluster.Contains(c.Name)), c => Assert.True(c.Healthy, c.Name));
        Assert.All(checks.Where(c => app.Contains(c.Name)), c => Assert.False(c.Healthy, c.Name));
        Assert.Equal("service bus gave no answer within 0.5 s", Named(checks, "service bus").Error);
        Assert.Equal("cosmos gave no answer within 0.5 s", Named(checks, "cosmos").Error);
        Assert.Equal("mongo gave no answer within 0.5 s", Named(checks, "mongo").Error);
    }

    [Fact]
    public async Task Mongo_as_the_live_store_calls_no_Cosmos()
    {
        HealthyCluster();

        var checks = await Run(Reading(store: "mongo"));

        Assert.DoesNotContain(checks, c => c.Name == "cosmos");
        Assert.Contains(checks, c => c.Name == "mongo");
    }

    [Theory]
    [InlineData("cosmos")]
    [InlineData(null)]
    public async Task Cosmos_stays_unless_the_live_store_is_known_to_be_Mongo(string? store)
    {
        HealthyCluster();

        var checks = await Run(Reading(store: store));

        Assert.Contains(checks, c => c.Name == "cosmos");
    }

    [Fact]
    public async Task A_refusal_inside_the_limit_keeps_the_service_error()
    {
        HealthyCluster();
        var metrics = new MetricSeries((_, _) => Task.FromResult(History));
        var checks = new ReportChecks(cluster.Client(), new MachineChecks(Origin, "token", TimeProvider.System), metrics, Nowhere, MongoNowhere, Origin,
            ["w1"], [], App, [], TimeSpan.FromSeconds(12));

        var results = await Task.WhenAll(checks.All(Reading(), []));

        var cosmos = Named(results, "cosmos");
        Assert.False(cosmos.Healthy);
        Assert.DoesNotContain("gave no answer", cosmos.Error);
        Assert.DoesNotContain("canceled", cosmos.Error);
        Assert.Equal("service bus gave no answer within 12 s", Named(results, "service bus").Error);
        var mongo = Named(results, "mongo");
        Assert.False(mongo.Healthy);
        Assert.DoesNotContain("gave no answer", mongo.Error);
        Assert.DoesNotContain("canceled", mongo.Error);
    }

    [Fact]
    public async Task With_the_app_its_checks_follow_and_network_rows_come_as_read()
    {
        HealthyCluster();
        HealthyApp();

        var checks = await Run(Reading(app: true), [new NetCheck("net a", null), new NetCheck("net b", "refused")]);

        Assert.Equal(
            [
                "control plane", "node w1", $"workloads {System}", "ingress address", "service bus", "cosmos", "mongo", "caps", "prometheus",
                $"workloads {App}", "nightly job", "net a", "net b", "app api ready", "dispatcher sweep", "page /", "ingress /ws", "ingress /line", "line bundles", "ingress /ws/hops"
            ],
            checks.Select(c => c.Name));
        Assert.True(Named(checks, $"workloads {App}").Healthy);
        Assert.True(Named(checks, "nightly job").Healthy);
        Assert.True(Named(checks, "app api ready").Healthy);
        Assert.Equal(new Check("net a", true, null), Named(checks, "net a"));
        Assert.Equal(new Check("net b", false, "refused"), Named(checks, "net b"));
    }

    private const string Holder = "dispatcher-node2";
    private static string HolderStatus => $"/api/v1/namespaces/{App}/pods/{Holder}:8080/proxy/status";

    [Fact]
    public async Task REQ_BT_019_The_lease_holder_sweeping_reads_healthy()
    {
        HealthyCluster();
        HealthyApp();
        cluster.Answer(HolderStatus, """{"leader":true,"connected":true,"secondsSinceSweep":3.2}""");

        var checks = await Run(Reading(app: true, leader: Holder));

        Assert.Equal(new Check("dispatcher sweep", true, null), Named(checks, "dispatcher sweep"));
    }

    [Fact]
    public async Task REQ_BT_019_A_lease_renewing_with_no_term_running_reads_failing()
    {
        HealthyCluster();
        HealthyApp();
        cluster.Answer(HolderStatus, """{"leader":false,"connected":false,"secondsSinceSweep":null}""");

        var checks = await Run(Reading(app: true, leader: Holder));

        Assert.Equal($"{Holder} holds the lease but is not leading, so nothing sweeps", Named(checks, "dispatcher sweep").Error);
    }

    [Fact]
    public async Task REQ_BT_019_A_lease_with_no_holder_reads_failing()
    {
        HealthyCluster();
        HealthyApp();

        var checks = await Run(Reading(app: true));

        Assert.Equal("lease names no live Dispatcher, or could not be read", Named(checks, "dispatcher sweep").Error);
    }

    [Fact]
    public async Task REQ_BT_019_A_holder_that_cannot_be_read_reads_failing()
    {
        HealthyCluster();
        HealthyApp();
        cluster.Fail(HolderStatus, global::System.Net.HttpStatusCode.ServiceUnavailable);

        var checks = await Run(Reading(app: true, leader: Holder));

        Assert.False(Named(checks, "dispatcher sweep").Healthy);
    }

    [Fact]
    public async Task A_broken_app_keeps_its_checks_in_the_report_and_each_says_why()
    {
        HealthyCluster();
        cluster.Answer($"/apis/apps/v1/namespaces/{App}/deployments", List());
        cluster.Answer($"/apis/apps/v1/namespaces/{App}/daemonsets", List());
        cluster.Answer($"/apis/batch/v1/namespaces/{App}/cronjobs/nightly", """{"metadata":{"name":"nightly"},"spec":{"schedule":"0 0 * * *","jobTemplate":{},"suspend":true}}""");
        cluster.Answer($"/apis/batch/v1/namespaces/{App}/jobs", List());
        cluster.Fail($"/api/v1/namespaces/{App}/services/http:api:8080/proxy/api/ready/deep", global::System.Net.HttpStatusCode.ServiceUnavailable);

        var checks = await Run(Reading(app: true));

        Assert.Equal("nothing deployed", Named(checks, $"workloads {App}").Error);
        Assert.Equal("suspended", Named(checks, "nightly job").Error);
        Assert.False(Named(checks, "app api ready").Healthy);
    }

    [Fact]
    public async Task Each_broken_cluster_check_says_why_and_a_line_break_in_the_reason_goes()
    {
        cluster.Answer("/api/v1/nodes/w1", Node("w1", "False"));
        cluster.Answer($"/apis/apps/v1/namespaces/{System}/deployments", List(Deployment("short", 2, 1), Deployment("zero", 0, 0), Deployment("fine", 1, 1)));
        cluster.Answer($"/apis/apps/v1/namespaces/{System}/daemonsets", List(DaemonSet("partial", 2, 1), DaemonSet("none", 0, 0), DaemonSet("whole", 1, 1)));
        cluster.Answer($"/apis/autoscaling/v2/namespaces/{System}/horizontalpodautoscalers", List());
        cluster.Answer("/api/v1/namespaces/ingress-nginx/services/ingress-nginx-controller", """{"metadata":{"name":"ingress-nginx-controller"},"status":{"loadBalancer":{}}}""");

        var checks = await Run(Reading(answered: false, error: "api\ndown"), prometheusFails: "no prometheus");

        Assert.Equal("api down", Named(checks, "control plane").Error);
        Assert.Equal("not Ready", Named(checks, "node w1").Error);
        Assert.Equal("not ready: short,zero,partial,none", Named(checks, $"workloads {System}").Error);
        Assert.Equal("no external address", Named(checks, "ingress address").Error);
        Assert.Equal("api down", Named(checks, "caps").Error);
        Assert.Equal("no prometheus", Named(checks, "prometheus").Error);
    }

    [Fact]
    public async Task Caps_lists_every_breach_then_every_issue()
    {
        HealthyCluster();
        CapRow[] rows = [new("w1", "cpuMilli", 1000, 1500, true), new("w1", "memoryMi", 1000, 900, false), new("w2", "memoryMi", 1000, 1001, true)];

        var checks = await Run(Reading(caps: rows, issues: ["api: 3 pods, file allows 2"]));

        Assert.Equal("w1 cpuMilli 1500 over 1000; w2 memoryMi 1001 over 1000; api: 3 pods, file allows 2", Named(checks, "caps").Error);
    }

    [Fact]
    public async Task One_stuck_check_ends_at_its_own_limit_and_holds_no_other()
    {
        HealthyCluster();
        var stuck = new FakeCluster();
        stuck.Hang("/api/v1/nodes/w1");
        var metrics = new MetricSeries((_, _) => Task.FromResult(History));
        var checks = new ReportChecks(stuck.Client(), new MachineChecks(Origin, "token", TimeProvider.System), metrics, Nowhere, MongoNowhere, Origin,
            ["w1"], [], App, [], TimeSpan.FromMilliseconds(200));

        var running = checks.All(Reading(), []).ToList();
        var node = await running[1].WaitAsync(TimeSpan.FromSeconds(10));
        var results = await Task.WhenAll(running);

        Assert.False(node.Healthy);
        Assert.Equal("node w1", node.Name);
        Assert.Equal("node w1 gave no answer within 0.2 s", node.Error);
        Assert.True(Named(results, "prometheus").Healthy);
        Assert.True(Named(results, "caps").Healthy);
    }
}
