using System.Text.Json;
using Xunit;
using static FakeCluster;

public class ClusterWatchesTests
{
    private const string Nodes = "/api/v1/nodes";
    private const string Pods = "/api/v1/pods";

    private readonly FakeCluster cluster = new();
    private readonly ChangeFeed feed;
    private readonly Operations operations;
    private int changed;

    public ClusterWatchesTests()
    {
        var hub = new SocketHub(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        feed = new ChangeFeed(hub);
        operations = new Operations(hub);
    }

    private (string Kind, string Target, string? Old, string? New)[] Changes()
    {
        var frame = JsonSerializer.SerializeToElement(feed.Frame(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return frame.GetProperty("changes").EnumerateArray().Select(c => (
            c.GetProperty("kind").GetString()!, c.GetProperty("object").GetString()!,
            c.GetProperty("old").GetString(), c.GetProperty("new").GetString())).ToArray();
    }

    /// <summary>Follows until the report was told of a change this many times, then stops every watch.</summary>
    private async Task FollowUntil(int changes)
    {
        using var stop = new CancellationTokenSource();
        var loops = new Loops(_ => { }, TimeSpan.FromMilliseconds(1), _ => { });
        new ClusterWatches(cluster.Client(), feed, operations, loops, () => Interlocked.Increment(ref changed)).Start(stop.Token);
        var limit = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref changed) < changes && DateTime.UtcNow < limit) await Task.Delay(20);
        await stop.CancelAsync();
    }

    [Fact]
    public async Task The_first_list_only_seeds_so_a_start_reports_nothing()
    {
        cluster.Answer(Nodes, List(Node("n1")));
        cluster.Answer(Watch(Nodes), Event("MODIFIED", Node("n1", "False")));

        await FollowUntil(1);

        Assert.Equal([("node ready", "n1", (string?)"True", (string?)"False")], Changes());
    }

    [Fact]
    public async Task Each_event_compares_against_what_was_known_and_a_bookmark_changes_nothing()
    {
        cluster.Answer(Nodes, List(Node("n1"), Node("n2")));
        cluster.Answer(Watch(Nodes), string.Join("\n",
            Event("MODIFIED", Node("n1", "False")),
            Event("BOOKMARK", Node("n1", "Unknown")),
            Event("ADDED", Node("n3")),
            Event("DELETED", Node("n2")),
            Event("MODIFIED", Node("n1"))));

        await FollowUntil(4);

        Assert.Equal(
            [
                ("node ready", "n1", (string?)"True", (string?)"False"),
                ("node added", "n3", null, "True"),
                ("node removed", "n2", null, null),
                ("node ready", "n1", "False", "True")
            ],
            Changes());
        Assert.Equal(4, changed);
    }

    [Fact]
    public async Task A_deleted_item_is_forgotten_so_its_return_is_an_addition()
    {
        cluster.Answer(Nodes, List(Node("n1")));
        cluster.Answer(Watch(Nodes), string.Join("\n", Event("DELETED", Node("n1")), Event("ADDED", Node("n1"))));

        await FollowUntil(2);

        Assert.Equal([("node removed", "n1", (string?)null, (string?)null), ("node added", "n1", null, "True")], Changes());
    }

    [Fact]
    public async Task A_pod_shows_as_deleted_when_deletion_starts_while_it_is_still_there()
    {
        cluster.Answer(Pods, List(Pod("app", "web-1")));
        cluster.Answer(Watch(Pods), Event("MODIFIED", Pod("app", "web-1", deleted: true)));

        await FollowUntil(1);

        Assert.Equal([("pod deleted", "app/web-1", (string?)"w1", (string?)null)], Changes());
    }

    [Fact]
    public async Task A_pod_that_shows_as_deleted_does_not_show_again_when_it_is_gone()
    {
        cluster.Answer(Pods, List(Pod("app", "web-1")));
        cluster.Answer(Watch(Pods), string.Join("\n",
            Event("MODIFIED", Pod("app", "web-1", deleted: true)),
            Event("DELETED", Pod("app", "web-1", deleted: true))));

        await FollowUntil(2);

        Assert.Equal([("pod deleted", "app/web-1", (string?)"w1", (string?)null)], Changes());
    }

    [Fact]
    public async Task A_pod_removed_with_no_deletion_seen_still_shows_as_deleted()
    {
        cluster.Answer(Pods, List(Pod("app", "web-1")));
        cluster.Answer(Watch(Pods), Event("DELETED", Pod("app", "web-1")));

        await FollowUntil(1);

        Assert.Equal([("pod deleted", "app/web-1", (string?)"w1", (string?)null)], Changes());
    }

    [Fact]
    public async Task A_reconnect_lists_again_so_a_missed_change_still_shows()
    {
        cluster.Answer(Nodes, List(Node("n1"), Node("n2")), List(Node("n1", "False"), Node("n3")));
        cluster.Answer(Watch(Nodes), "");

        await FollowUntil(3);

        Assert.Equal(
            [
                ("node ready", "n1", (string?)"True", (string?)"False"),
                ("node added", "n3", null, "True"),
                ("node removed", "n2", null, null)
            ],
            Changes());
    }

    [Fact]
    public async Task A_relist_tells_the_report_for_every_item_even_when_nothing_differs()
    {
        cluster.Answer(Nodes, List(Node("n1"), Node("n2")));
        cluster.Answer(Watch(Nodes), "");

        await FollowUntil(2);

        Assert.Empty(Changes());
        Assert.Equal(2, changed);
    }

    [Fact]
    public async Task A_failed_list_is_retried_and_the_list_after_it_only_seeds()
    {
        cluster.Fail(Nodes);
        cluster.Answer(Nodes, List(Node("n1")));
        cluster.Answer(Watch(Nodes), Event("MODIFIED", Node("n1", "False")));

        await FollowUntil(1);

        Assert.Equal([("node ready", "n1", (string?)"True", (string?)"False")], Changes());
    }
}
