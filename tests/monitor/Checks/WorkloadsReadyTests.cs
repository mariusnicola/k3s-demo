using Xunit;
using static FakeCluster;

/// <summary>When a scaled Deployment counts as failing, read through the real check.</summary>
public class WorkloadsReadyTests
{
    private const string Space = "kube-system";
    private const string Nowhere = "127.0.0.2";
    private const string Origin = $"http://{Nowhere}:1";
    private const string Mongo = $"mongodb://{Nowhere}:27018/?serverSelectionTimeoutMS=3000&retryWrites=false";
    private const string Deployments = $"/apis/apps/v1/namespaces/{Space}/deployments";
    private const string Scalers = $"/apis/autoscaling/v2/namespaces/{Space}/horizontalpodautoscalers";

    private readonly FakeCluster cluster = new();

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Worker(int spec, int ready) =>
        """{"metadata":{"name":"worker"},"spec":{"replicas":@spec,"selector":{},"template":{}},"status":{"readyReplicas":@ready}}"""
            .Replace("@spec", spec.ToString()).Replace("@ready", ready.ToString());

    private static string Scaler(int minimum) =>
        """{"metadata":{"name":"keda-hpa-worker"},"spec":{"minReplicas":@minimum,"maxReplicas":5,"scaleTargetRef":{"kind":"Deployment","name":"worker"}}}"""
            .Replace("@minimum", minimum.ToString());

    private ReportChecks Checks(TimeProvider clock, int minimum, params (int Spec, int Ready)[] reads)
    {
        cluster.Answer($"/apis/apps/v1/namespaces/{Space}/daemonsets", List());
        cluster.Answer(Scalers, List(Scaler(minimum)));
        cluster.Answer(Deployments, reads.Select(r => List(Worker(r.Spec, r.Ready))).ToArray());
        var metrics = new MetricSeries((_, _) => Task.FromResult("{}"));
        return new ReportChecks(cluster.Client(), new MachineChecks(Origin, "token", TimeProvider.System), metrics, Nowhere, Mongo, Origin,
            [], [Space], "batch-tracker", [], TimeSpan.FromMilliseconds(500), clock);
    }

    private async Task<Check> Workloads(ReportChecks checks) =>
        (await Task.WhenAll(checks.All(new Snapshot([], [], [], [], false, true, null), []))).Single(c => c.Name == $"workloads {Space}");

    [Fact]
    public async Task REQ_BT_026_A_scale_up_above_the_minimum_is_no_failure_until_the_limit()
    {
        var clock = new Clock();
        var checks = Checks(clock, 1, (5, 2));

        Assert.True((await Workloads(checks)).Healthy);
        clock.Now += ReadyRule.ScaleUpLimit - TimeSpan.FromSeconds(1);
        Assert.True((await Workloads(checks)).Healthy);
        clock.Now += TimeSpan.FromSeconds(1);

        Assert.Equal("not ready: worker", (await Workloads(checks)).Error);
    }

    [Fact]
    public async Task REQ_BT_026_Reaching_spec_starts_the_limit_again()
    {
        var clock = new Clock();
        var checks = Checks(clock, 1, (5, 2), (5, 5), (5, 2));

        await Workloads(checks);
        clock.Now += ReadyRule.ScaleUpLimit - TimeSpan.FromSeconds(1);
        Assert.True((await Workloads(checks)).Healthy);
        clock.Now += ReadyRule.ScaleUpLimit - TimeSpan.FromSeconds(1);

        Assert.True((await Workloads(checks)).Healthy);
    }

    [Fact]
    public async Task REQ_BT_026_Ready_below_the_minimum_fails_at_once()
    {
        var checks = Checks(new Clock(), 2, (5, 1));

        Assert.Equal("not ready: worker", (await Workloads(checks)).Error);
    }

    [Fact]
    public async Task REQ_BT_026_A_scaled_Deployment_with_nothing_ready_fails_at_once()
    {
        var checks = Checks(new Clock(), 1, (5, 0));

        Assert.Equal("not ready: worker", (await Workloads(checks)).Error);
    }

    [Fact]
    public async Task REQ_BT_026_A_failed_scaler_read_fails_the_check()
    {
        var checks = Checks(new Clock(), 1, (5, 5));
        cluster.Reset(Scalers);
        cluster.Fail(Scalers);

        Assert.False((await Workloads(checks)).Healthy);
    }
}
