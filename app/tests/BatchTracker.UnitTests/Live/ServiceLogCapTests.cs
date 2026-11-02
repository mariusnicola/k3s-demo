using BatchTracker.Application;
using BatchTracker.Live;

namespace BatchTracker.UnitTests;

/// <summary>Browser rows sit behind their own cap, so an anonymous report never pushes out a service row.</summary>
public class ServiceLogCapTests : ServiceLogTest
{
    private static readonly ServiceLogQuery Everything = new(Limit: ServiceLog.Capacity);

    /// <summary>Adds this many browser reports, each with its own id.</summary>
    private static void Flood(ServiceLog log, int rows)
    {
        for (var i = 0; i < rows; i++) log.Add(Report($"flood-{i}", null));
    }

    [Fact]
    public void REQ_BT_050_A_post_that_comes_again_adds_nothing_and_another_post_does()
    {
        var log = new ServiceLog();

        log.AddPost("post-1", [Row("first")]);
        log.AddPost("post-1", [Row("first")]);
        log.AddPost("post-2", [Row("first")]);

        Assert.Equal(2, log.Query(Everything).Entries.Count);
    }

    [Fact]
    public void REQ_BT_050_Remembered_posts_are_bounded()
    {
        var log = new ServiceLog();
        log.AddPost("old", [Row("old")]);
        for (var i = 0; i < ServiceLog.RememberedPosts; i++) log.AddPost($"post-{i}", [Row("flood")]);

        log.AddPost("old", [Row("old")]);

        Assert.Single(log.Query(new ServiceLogQuery(CorrelationId: "old")).Entries);
    }

    [Fact]
    public void REQ_BT_056_A_browser_flood_leaves_a_real_service_error_in_place()
    {
        var log = new ServiceLog();
        log.Add(Row("real-error"));

        Flood(log, ServiceLog.Capacity + 1);

        Assert.Contains(log.Query(new ServiceLogQuery()).Entries, e => e.CorrelationId == "real-error");
    }

    [Fact]
    public void REQ_BT_056_The_limit_applies_to_each_queue_and_the_counts_stay_honest()
    {
        var log = new ServiceLog();
        for (var i = 0; i < 3; i++) log.Add(Row($"row-{i}"));
        Flood(log, 3);

        var page = log.Query(new ServiceLogQuery(Limit: 2));

        Assert.Equal(2, page.Entries.Count(e => e.Service == ServiceLogLevel.BrowserService));
        Assert.Equal(2, page.Entries.Count(e => e.Service != ServiceLogLevel.BrowserService));
        Assert.Equal(6, page.Matched);
        Assert.Equal(2, page.DroppedByLimit);
    }

    [Fact]
    public void REQ_BT_056_A_browser_failure_filter_still_reads_browser_rows_up_to_the_limit()
    {
        var log = new ServiceLog();
        log.Add(Row("real-error"));
        Flood(log, 3);

        var page = log.Query(new ServiceLogQuery(Reason: ServiceLogLevel.BrowserReason, Limit: 2));

        Assert.Equal(["flood-2", "flood-1"], page.Entries.Select(e => e.ReportId));
        Assert.Equal(3, page.Matched);
        Assert.Equal(1, page.DroppedByLimit);
    }

    [Fact]
    public void REQ_BT_056_Browser_rows_stop_at_their_cap_and_count_their_own_evictions()
    {
        var log = new ServiceLog();

        Flood(log, ServiceLog.BrowserCapacity + 5);

        var page = log.Query(Everything);
        Assert.Equal(ServiceLog.BrowserCapacity, page.Matched);
        Assert.Equal(5, page.BrowserEvicted);
        Assert.Equal(0, page.Evicted);
        Assert.Equal("flood-5", page.Entries[^1].ReportId);
    }

    [Fact]
    public void REQ_BT_056_Service_rows_past_their_cap_leave_browser_rows_alone()
    {
        var log = new ServiceLog();
        log.Add(Report("kept", null));

        for (var i = 0; i < ServiceLog.Capacity + 1; i++) log.Add(Row($"row-{i}"));

        var page = log.Query(new ServiceLogQuery(ReportId: "kept"));
        Assert.Equal("kept", Assert.Single(page.Entries).ReportId);
        Assert.Equal(1, page.Evicted);
        Assert.Equal(0, page.BrowserEvicted);
    }

    [Fact]
    public void REQ_BT_056_Browser_and_service_rows_merge_by_time_newest_first()
    {
        var log = new ServiceLog();
        var start = DateTimeOffset.UnixEpoch;
        log.Add(Row("first") with { At = start });
        log.Add(Row("third") with { At = start.AddSeconds(2) });
        log.Add(Report("second", null) with { At = start.AddSeconds(1), CorrelationId = "second" });

        var page = log.Query(Everything);

        Assert.Equal(["third", "second", "first"], page.Entries.Select(e => e.CorrelationId));
    }

    [Fact]
    public void REQ_BT_056_A_report_id_finds_its_request_rows_across_the_two_queues()
    {
        var log = new ServiceLog();
        log.Add(Row("req-7"));
        log.Add(Report("wanted", "req-7"));

        Flood(log, ServiceLog.BrowserCapacity - 1);

        var page = log.Query(new ServiceLogQuery(ReportId: "wanted"));
        Assert.Equal(2, page.Matched);
    }
}
