using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;
using static FakeCluster;

/// <summary>The two values the Workers card needs: how many Workers can run, and which are working now.</summary>
public sealed class WorkerFleetTests : IDisposable
{
    private const string App = "batch-tracker";
    private const string Gauge = "bt_worker_work_in_flight";

    private const string Caps = """
        {"nodes":{"w1":{"cpuMilli":1000,"memoryMi":1000}},"reserved":[],
         "pods":[{"name":"worker","node":"w1","cpuMilli":100,"memoryMi":100,"namespace":"batch-tracker","pod":"^worker-(?!node2-)","container":"worker","replicas":2},
                 {"name":"worker-node2","node":"w1","cpuMilli":100,"memoryMi":100,"namespace":"batch-tracker","pod":"^worker-node2-","container":"worker","replicas":1},
                 {"name":"api","node":"w1","cpuMilli":100,"memoryMi":100,"namespace":"batch-tracker","pod":"^api-","container":"c","replicas":2}]}
        """;

    private readonly FakeCluster cluster = new();
    private readonly string capsPath = Path.GetTempFileName();

    public WorkerFleetTests()
    {
        File.WriteAllText(capsPath, Caps);
        cluster.Answer("/api/v1/nodes", List(Node("w1")));
        cluster.Answer("/api/v1/pods", List(Pod(App, "worker-1"), Pod(App, "worker-2"), Pod(App, "api-1")));
        cluster.Answer("/api/v1/namespaces", List("""{"metadata":{"name":"batch-tracker"}}"""));
    }

    public void Dispose() => File.Delete(capsPath);

    private ClusterReader Reader(Func<CancellationToken, Task<Dictionary<string, bool?>>> busy) =>
        new(cluster.Client(), "control", App, [], TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500), capsPath, busy);

    private static Task<Dictionary<string, bool?>> Busy(params (string Pod, bool Working)[] pods) =>
        Task.FromResult(pods.ToDictionary(p => p.Pod, p => (bool?)p.Working));

    private static string Answer(params (string Pod, string Value)[] rows)
    {
        var results = rows.Select(r => $$"""{"metric":{"__name__":"{{Gauge}}","pod":"{{r.Pod}}"},"value":[1,"{{r.Value}}"]}""");
        return """{"status":"success","data":{"resultType":"vector","result":[""" + string.Join(",", results) + "]}}";
    }

    private static bool? BusyOf(Snapshot snapshot, string pod) => snapshot.Pods.Single(p => p.Name == pod).Busy;

    [Fact]
    public async Task REQ_BT_026_Most_possible_adds_every_caps_entry_that_runs_the_worker_container()
    {
        var snapshot = await Reader(_ => Busy()).Collect();

        Assert.Equal(new WorkerFleet(3), snapshot.Workers);
    }

    [Fact]
    public void REQ_BT_026_Most_possible_on_the_real_caps_file_is_the_scaled_workers_plus_the_fixed_one()
    {
        var path = RealCaps();
        using var raw = JsonDocument.Parse(File.ReadAllText(path));
        var entries = raw.RootElement.GetProperty("pods").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);

        var expected = entries["worker"].GetProperty("replicas").GetInt32() + entries["worker-node2"].GetProperty("replicas").GetInt32();

        Assert.Equal(expected, CapsFile.Load(path).WorkerMax(App));
    }

    private static string RealCaps([CallerFilePath] string test = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(test)!, "..", "..", "..", "deploy", "caps.json"));

    [Fact]
    public async Task REQ_BT_026_A_caps_file_with_no_worker_entry_reports_no_number_rather_than_zero()
    {
        File.WriteAllText(capsPath, """{"nodes":{"w1":{"cpuMilli":1,"memoryMi":1}},"reserved":[],"pods":[]}""");

        var snapshot = await Reader(_ => Busy()).Collect();

        Assert.Null(snapshot.Error);
        Assert.Null(snapshot.Workers);
    }

    [Fact]
    public async Task REQ_BT_026_A_failed_cluster_read_reports_no_number()
    {
        File.Delete(capsPath);

        var snapshot = await Reader(_ => Busy()).Collect();

        Assert.NotNull(snapshot.Error);
        Assert.Null(snapshot.Workers);
    }

    [Fact]
    public async Task REQ_BT_026_A_Worker_reads_busy_or_idle_from_the_gauge_and_any_other_pod_reads_nothing()
    {
        var snapshot = await Reader(_ => Busy(("worker-1", true), ("worker-2", false))).Collect();

        Assert.True(BusyOf(snapshot, "worker-1"));
        Assert.False(BusyOf(snapshot, "worker-2"));
        Assert.Null(BusyOf(snapshot, "api-1"));
    }

    [Fact]
    public async Task REQ_BT_026_A_Worker_with_no_sample_is_unknown_never_idle()
    {
        var snapshot = await Reader(_ => Busy(("worker-1", true))).Collect();

        Assert.Null(BusyOf(snapshot, "worker-2"));
    }

    [Fact]
    public async Task REQ_BT_026_Prometheus_down_leaves_every_Worker_unknown_and_keeps_the_report()
    {
        var snapshot = await Reader(_ => throw new HttpRequestException("refused")).Collect();

        Assert.Null(snapshot.Error);
        Assert.Equal(3, snapshot.Pods.Length);
        Assert.All(snapshot.Pods, p => Assert.Null(p.Busy));
        Assert.Equal(new WorkerFleet(3), snapshot.Workers);
    }

    [Fact]
    public async Task REQ_BT_026_A_stuck_Prometheus_ends_at_the_usage_limit_with_every_Worker_unknown()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var snapshot = await Reader(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
            return [];
        }).Collect();

        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 10);
        Assert.All(snapshot.Pods, p => Assert.Null(p.Busy));
    }

    private static MetricSeries Sources(string? gauge, string? passed) => new((path, _) =>
    {
        var answer = Uri.UnescapeDataString(path).Contains(Gauge) ? gauge : passed;
        return answer is null ? throw new HttpRequestException("refused") : Task.FromResult(answer);
    });

    [Theory]
    [InlineData("0", "0.5", true)]
    [InlineData("1", "0", true)]
    [InlineData("0", "0", false)]
    public async Task REQ_BT_026_Busy_is_true_if_either_source_says_so_and_false_only_if_both_say_no(string inFlight, string passed, bool expected)
    {
        var busy = await Sources(Answer(("worker-1", inFlight)), Answer(("worker-1", passed))).WorkerBusy(default);

        Assert.Equal(new Dictionary<string, bool?> { ["worker-1"] = expected }, busy);
    }

    [Fact]
    public async Task REQ_BT_026_The_passed_rate_is_summed_across_farms_per_pod_over_one_minute()
    {
        var asked = new List<string>();
        var series = new MetricSeries((path, _) =>
        {
            asked.Add(Uri.UnescapeDataString(path));
            return Task.FromResult(Answer(("worker-1", "0")));
        });

        await series.WorkerBusy(default);

        Assert.Contains(asked, q => q.Contains("sum by (pod) (rate(bt_events_passed_total[1m]))"));
    }

    [Fact]
    public async Task REQ_BT_026_A_failed_gauge_read_with_no_passed_events_is_unknown_and_a_passed_event_is_still_busy()
    {
        var busy = await Sources(null, Answer(("worker-1", "0"), ("worker-2", "3"))).WorkerBusy(default);

        Assert.Equal(new Dictionary<string, bool?> { ["worker-1"] = null, ["worker-2"] = true }, busy);
    }

    [Fact]
    public async Task REQ_BT_026_A_failed_rate_read_with_nothing_in_flight_is_unknown_and_work_in_flight_is_still_busy()
    {
        var busy = await Sources(Answer(("worker-1", "0"), ("worker-2", "1")), null).WorkerBusy(default);

        Assert.Equal(new Dictionary<string, bool?> { ["worker-1"] = null, ["worker-2"] = true }, busy);
    }

    [Fact]
    public async Task REQ_BT_026_A_pod_missing_from_one_answer_is_unknown_unless_the_other_says_busy()
    {
        var busy = await Sources(Answer(("worker-1", "0"), ("worker-2", "1")), Answer(("worker-3", "0"))).WorkerBusy(default);

        Assert.Equal(new Dictionary<string, bool?> { ["worker-1"] = null, ["worker-2"] = true, ["worker-3"] = null }, busy);
    }

    [Fact]
    public async Task REQ_BT_026_Both_reads_failing_throws_so_nothing_reads_as_idle()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sources(null, null).WorkerBusy(default));
    }

    [Fact]
    public async Task REQ_BT_026_A_refused_query_counts_as_a_failed_read()
    {
        var refused = """{"status":"error","error":"bad"}""";

        var busy = await Sources(refused, Answer(("worker-1", "0"))).WorkerBusy(default);

        Assert.Equal(new Dictionary<string, bool?> { ["worker-1"] = null }, busy);
    }

    [Fact]
    public void REQ_BT_026_The_report_sends_workers_max_and_busy_by_these_names()
    {
        var report = new Report(DateTimeOffset.UnixEpoch, true, [], [], [new PodRow(App, "worker-1", "w1", "Running", true, 0, 0, null, null, Busy: true)], [], [], Workers: new WorkerFleet(3));

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"workers\":{\"max\":3}", json);
        Assert.Contains("\"busy\":true", json);
    }

    [Fact]
    public void REQ_BT_026_An_unknown_Worker_is_sent_as_null_and_not_left_out()
    {
        var json = JsonSerializer.Serialize(new PodRow(App, "worker-1", "w1", "Running", true, 0, 0, null, null), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"busy\":null", json);
    }

    [Fact]
    public async Task REQ_BT_026_A_cycle_and_a_refresh_both_carry_the_most_possible()
    {
        var published = new List<Report>();
        var read = new Snapshot([], [], [], [], true, true, null, Workers: new WorkerFleet(3));
        var cycle = new ReportCycle(() => Task.FromResult(read), () => published.LastOrDefault(), published.Add);

        await cycle.Run(_ => Task.FromResult<Check[]>([]));
        Assert.Equal(new WorkerFleet(3), published[^1].Workers);

        read = read with { Workers = new WorkerFleet(4) };
        await cycle.Refresh();
        Assert.Equal(new WorkerFleet(4), published[^1].Workers);
    }
}
