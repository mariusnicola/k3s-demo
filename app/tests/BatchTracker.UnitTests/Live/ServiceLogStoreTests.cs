using BatchTracker.Application;
using BatchTracker.Infrastructure;
using BatchTracker.Live;
using BatchTracker.Line;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BatchTracker.UnitTests;

/// <summary>What Live holds and how the filters find it.</summary>
public class ServiceLogStoreTests : ServiceLogTest
{
    [Fact]
    public void REQ_BT_053_Live_keeps_only_the_newest_entries()
    {
        var log = new ServiceLog();
        for (var i = 0; i < ServiceLog.Capacity + 7; i++) log.Add(Entry());

        var page = log.Query(new ServiceLogQuery(Limit: ServiceLog.Capacity));

        Assert.Equal(ServiceLog.Capacity, page.Matched);
        Assert.Equal(7, page.Evicted);
    }

    [Fact]
    public void REQ_BT_053_The_filters_narrow_the_list_newest_first()
    {
        var log = new ServiceLog();
        log.Add(Entry(at: DateTimeOffset.UnixEpoch));
        log.Add(Entry(ServiceLogLevel.Warning, "worker", "other", DateTimeOffset.UnixEpoch.AddMinutes(1)));

        Assert.Equal("worker", log.Query(new ServiceLogQuery()).Entries[0].Service);
        Assert.Equal(1, log.Query(new ServiceLogQuery(Level: ServiceLogLevel.Error)).Matched);
        Assert.Equal(1, log.Query(new ServiceLogQuery(Service: "worker")).Matched);
        Assert.Equal(1, log.Query(new ServiceLogQuery(CorrelationId: "  abc-1 ")).Matched);
        Assert.Equal(0, log.Query(new ServiceLogQuery(CorrelationId: "abc")).Matched);
        Assert.Equal(1, log.Query(new ServiceLogQuery(From: DateTimeOffset.UnixEpoch.AddSeconds(1))).Matched);
    }

    [Fact]
    public void REQ_BT_053_The_filters_of_the_line_log_work_here_too()
    {
        var log = new ServiceLog();
        log.Add(Entry());
        log.Add(Entry(ServiceLogLevel.Warning) with { Farm = "farm-a", BatchId = "b-1" });

        Assert.Equal(1, log.Query(new ServiceLogQuery(Reason: ServiceLogLevel.ErrorReason)).Matched);
        Assert.Equal(1, log.Query(new ServiceLogQuery(Farm: "farm-a")).Matched);
        Assert.Equal(1, log.Query(new ServiceLogQuery(Batch: "b-1")).Matched);
    }

    [Fact]
    public void REQ_BT_053_An_entry_carries_the_trace_of_its_action()
    {
        using var activity = new System.Diagnostics.Activity("test").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C).Start();

        Logger().LogError("Store failed");

        Assert.Equal(activity.TraceId.ToString(), Assert.Single(sink.Entries).TraceId);
    }

    [Theory]
    [InlineData("nope", null, "level")]
    [InlineData(null, 0, "limit")]
    [InlineData(null, ServiceLog.Capacity + 1, "limit")]
    [InlineData(null, null, "reason", "nope")]
    public void REQ_BT_053_Bad_filters_are_named(string? level, int? limit, string named, string? reason = null)
    {
        Assert.Contains(named, ServiceLog.Problem(new ServiceLogQuery(Level: level, Limit: limit, Reason: reason)));
    }

    [Fact]
    public void REQ_BT_056_A_report_id_finds_the_report_and_its_request_rows()
    {
        var log = new ServiceLog();
        log.Add(Report("r-1", "c-1"));
        log.Add(Row("c-1"));
        log.Add(Row("c-2"));
        log.Add(Report("r-2", "c-2"));

        var page = log.Query(new ServiceLogQuery(ReportId: "r-1"));

        Assert.Equal(2, page.Matched);
        Assert.All(page.Entries, e => Assert.Equal("c-1", e.CorrelationId));
    }

    [Fact]
    public void REQ_BT_056_A_report_with_no_request_finds_only_itself()
    {
        var log = new ServiceLog();
        log.Add(Report("r-1", null));
        log.Add(new ServiceLogEntry(DateTimeOffset.UnixEpoch, "error", "service-error", "No id", "api", "api-1", null, null, null));

        Assert.Equal("r-1", Assert.Single(log.Query(new ServiceLogQuery(ReportId: "r-1")).Entries).ReportId);
    }

    [Fact]
    public void REQ_BT_056_An_unknown_report_id_finds_nothing()
    {
        var log = new ServiceLog();
        log.Add(Row("c-1"));

        Assert.Empty(log.Query(new ServiceLogQuery(ReportId: "r-1")).Entries);
    }

    [Fact]
    public void REQ_BT_056_Holding_a_report_masks_and_cuts_its_fields()
    {
        var log = new ServiceLog();

        log.Add(Report("r-1", null) with { Route = "/" + new string('a', 500) + " AccountKey=abcdef0123456789", Kind = new string('k', 500) });

        var entry = Assert.Single(log.Query(new ServiceLogQuery()).Entries);
        Assert.True(entry.Route!.Length <= ServiceLog.MaxField);
        Assert.True(entry.Kind!.Length <= ServiceLog.MaxField);
        Assert.DoesNotContain("abcdef0123456789", entry.Route);
    }
}
