using System.Text.Json;
using Xunit;
using static FakeCluster;

/// <summary>A cycle takes the real two second retry here, so what a watch reads across a reconnect is measured, not assumed.</summary>
public sealed class WatchHealthTests : IDisposable
{
    private const string Nodes = "/api/v1/nodes";
    private const string Watcher = "watch nodes";
    private static readonly TimeSpan PastRetry = TimeSpan.FromMilliseconds(2600);

    private readonly FakeCluster cluster = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Loops loops = new(_ => { }, TimeSpan.FromMilliseconds(1), _ => { });
    private int changed;

    public void Dispose()
    {
        stop.Cancel();
        stop.Dispose();
    }

    private void Start()
    {
        var hub = new SocketHub(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        new ClusterWatches(cluster.Client(), new ChangeFeed(hub), new Operations(hub), loops, () => Interlocked.Increment(ref changed)).Start(stop.Token);
    }

    /// <summary>The other four watches have no fake answers and fail, so only this one's name is read.</summary>
    private bool Red() => (loops.Check().Error ?? "").Contains(Watcher);

    private static async Task Until(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(done(), what);
    }

    /// <summary>Every sample across the span, because a state that flips between two reports is what this file is about.</summary>
    private async Task<int> GreenSamples(TimeSpan span)
    {
        var green = 0;
        var end = DateTime.UtcNow + span;
        while (DateTime.UtcNow < end)
        {
            if (!Red()) green++;
            await Task.Delay(5);
        }
        return green;
    }

    /// <summary>The list hangs on the retry: an instant failure would turn red again before a sample could catch the wrong green.</summary>
    [Fact]
    public async Task REQ_BT_026_A_watch_whose_retry_gets_no_answer_stays_red()
    {
        cluster.Fail(Nodes);
        cluster.Hang(Nodes);

        Start();
        await Until(Red, "a dying watch never read red");

        Assert.Equal(0, await GreenSamples(PastRetry));
    }

    [Fact]
    public async Task REQ_BT_026_A_watch_that_fails_after_its_list_answered_reads_red()
    {
        cluster.Answer(Nodes, List(Node("n1")));
        cluster.Fail(Watch(Nodes));

        Start();

        await Until(Red, "a failing watch never read red");
    }

    [Fact]
    public async Task REQ_BT_026_A_watch_back_on_a_quiet_cluster_reads_green_with_no_event()
    {
        cluster.Fail(Nodes);
        cluster.Answer(Nodes, List(Node("n1")));
        cluster.Hang(Watch(Nodes));

        Start();
        await Until(Red, "the first failure never read red");

        await Until(() => !Red(), "a reconnected watch stayed red");
    }

    /// <summary>Runs until the relist after the end told the report, which is the reconnect, and returns any red seen on the way.</summary>
    private async Task<string?> RedUntilReconnected(int changes)
    {
        string? seen = null;
        Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref changed) < changes && DateTime.UtcNow < deadline)
        {
            if (Red()) seen = loops.Check().Error!.Split("; ").First(e => e.Contains(Watcher));
            await Task.Delay(5);
        }
        Assert.True(Volatile.Read(ref changed) >= changes, "it never reconnected");
        return seen;
    }

    /// <summary>An empty body is the real shape: the client reads the first line while opening, and throws on an end with none.</summary>
    [Fact]
    public async Task REQ_BT_026_A_quiet_watch_timing_out_with_no_event_never_reads_red()
    {
        cluster.Answer(Nodes, List(Node("n1")));
        cluster.Answer(Watch(Nodes), "");

        Assert.Null(await RedUntilReconnected(1));
    }

    [Fact]
    public async Task REQ_BT_026_A_watch_timing_out_after_an_event_never_reads_red()
    {
        cluster.Answer(Nodes, List(Node("n1")));
        cluster.Answer(Watch(Nodes), Event("MODIFIED", Node("n1", "False")));

        Assert.Null(await RedUntilReconnected(2));
    }

    [Fact]
    public async Task REQ_BT_026_Stopping_a_watch_is_no_failure()
    {
        cluster.Answer(Nodes, List(Node("n1")));
        cluster.Hang(Watch(Nodes));

        Start();
        await Task.Delay(100);
        await stop.CancelAsync();
        await Task.Delay(100);

        Assert.False(Red());
    }
}
