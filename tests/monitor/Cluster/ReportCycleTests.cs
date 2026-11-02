using Xunit;

public sealed class ReportCycleTests
{
    private readonly List<Report> published = [];
    private readonly Queue<Func<Task<Snapshot>>> reads = new();

    private ReportCycle Cycle() => new(() => reads.Dequeue()(), () => published.LastOrDefault(), published.Add);

    private static Snapshot Pods(params string[] names) =>
        new([], names.Select(n => new PodRow("app", n, "w1", "Running", true, 0, 0, null, null)).ToArray(), [], [], true, true, null);

    private static Snapshot Failed() => new([], [], [], [], false, false, "api down");

    private void Reads(params Snapshot[] snapshots)
    {
        foreach (var snapshot in snapshots) reads.Enqueue(() => Task.FromResult(snapshot));
    }

    private static Report Holding(params string[] names) =>
        new(DateTimeOffset.UnixEpoch, true, [new Check("old", true, null)], [], Pods(names).Pods, [], []);

    private static Check[] Healthy() => [new Check("c", true, null)];

    private string[] LastPods() => published[^1].Pods.Select(p => p.Name).ToArray();

    [Fact]
    public async Task A_cycle_with_no_report_yet_publishes_before_its_checks_and_again_after()
    {
        Reads(Pods("a"));
        var cycle = Cycle();
        var publishedBeforeChecks = -1;

        await cycle.Run(_ =>
        {
            publishedBeforeChecks = published.Count;
            return Task.FromResult(Healthy());
        });

        Assert.Equal(1, publishedBeforeChecks);
        Assert.False(published[0].Healthy);
        Assert.True(published[1].Healthy);
        Assert.Equal(["a"], LastPods());
    }

    [Fact]
    public async Task A_cycle_shows_its_checks_on_the_pods_of_its_own_read()
    {
        published.Add(Holding("old"));
        Reads(Pods("a"));
        Snapshot? seen = null;

        await Cycle().Run(snapshot =>
        {
            seen = snapshot;
            return Task.FromResult(Healthy());
        });

        Assert.Equal("a", Assert.Single(seen!.Pods).Name);
        Assert.Equal(["a"], LastPods());
        Assert.Equal(["c"], published[^1].Checks.Select(c => c.Name));
    }

    [Fact]
    public async Task A_pod_read_while_the_checks_ran_stays_in_the_report_they_end_in()
    {
        published.Add(Holding("old"));
        Reads(Pods("a"), Pods("a", "new"));
        var cycle = Cycle();

        await cycle.Run(async _ =>
        {
            await cycle.Refresh();
            return Healthy();
        });

        Assert.Equal(["a", "new"], LastPods());
    }

    [Fact]
    public async Task A_read_that_started_first_and_ended_last_does_not_replace_a_newer_one()
    {
        published.Add(Holding("old"));
        var slow = new TaskCompletionSource<Snapshot>();
        reads.Enqueue(() => slow.Task);
        Reads(Pods("newer"));
        var cycle = Cycle();

        var first = cycle.Run(_ => Task.FromResult(Healthy()));
        await cycle.Refresh();
        slow.SetResult(Pods("older"));
        await first;

        Assert.Equal(["newer"], LastPods());
    }

    [Fact]
    public async Task A_refresh_keeps_the_checks_of_a_report_published_during_its_read()
    {
        published.Add(Holding("old"));
        var read = new TaskCompletionSource<Snapshot>();
        reads.Enqueue(() => read.Task);

        var refresh = Cycle().Refresh();
        published.Add(Holding("old") with { Checks = [new Check("newer", false, "x")], Healthy = false });
        read.SetResult(Pods("a"));
        await refresh;

        Assert.Equal(["newer"], published[^1].Checks.Select(c => c.Name));
        Assert.Equal(["a"], LastPods());
    }

    [Fact]
    public async Task A_refresh_carries_the_leader()
    {
        published.Add(Holding("old"));
        Reads(Pods("a") with { DispatcherLeader = "dispatcher-a" });

        await Cycle().Refresh();

        Assert.Equal("dispatcher-a", published[^1].DispatcherLeader);
    }

    [Fact]
    public async Task A_refresh_carries_the_store()
    {
        published.Add(Holding("old"));
        Reads(Pods("a") with { Store = "mongo" });

        await Cycle().Refresh();

        Assert.Equal("mongo", published[^1].Store);
    }

    [Fact]
    public async Task A_cycle_carries_the_store()
    {
        published.Add(Holding("old"));
        Reads(Pods("a") with { Store = "mongo" });

        await Cycle().Run(_ => Task.FromResult(Healthy()));

        Assert.Equal("mongo", published[^1].Store);
    }

    [Fact]
    public async Task A_cycle_carries_the_leader()
    {
        published.Add(Holding("old"));
        Reads(Pods("a") with { DispatcherLeader = "dispatcher-a" });

        await Cycle().Run(_ => Task.FromResult(Healthy()));

        Assert.Equal("dispatcher-a", published[^1].DispatcherLeader);
    }

    [Fact]
    public async Task A_refresh_whose_read_failed_leaves_the_map_as_it_is()
    {
        published.Add(Holding("old"));
        Reads(Failed());

        await Cycle().Refresh();

        Assert.Single(published);
    }

    [Fact]
    public async Task A_cycle_whose_read_failed_says_so_with_no_pods()
    {
        published.Add(Holding("old"));
        Reads(Failed());

        await Cycle().Run(_ => Task.FromResult(new[] { new Check("control plane", false, "api down") }));

        Assert.Empty(published[^1].Pods);
        Assert.False(published[^1].Healthy);
    }

    [Fact]
    public async Task A_refresh_before_the_first_report_reads_nothing()
    {
        await Cycle().Refresh();

        Assert.Empty(published);
    }
}
