using BatchTracker.Line;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

public class LineLogTests
{
    private static LineEntry Entry(
        string reason = LineReason.HttpRefused, long number = 1, string farm = "farm-a", int minute = 0, string? id = "id-1")
    {
        var level = LineReason.Errors.Contains(reason) ? LineLevel.Error : LineLevel.Info;
        return new LineEntry(Start.AddMinutes(minute), level, reason, "text", number, id, farm, "Dose1", 5, 409, null, null, "t");
    }

    private static LineLog Filled()
    {
        var log = new LineLog();
        log.Add(Entry(LineReason.Started, 1, "farm-a", 0));
        log.Add(Entry(LineReason.HttpRefused, 1, "farm-a", 1, "id-1"));
        log.Add(Entry(LineReason.EventTimeout, 2, "farm-b", 2, "id-2"));
        log.Add(Entry(LineReason.Closed, 3, "farm-b", 3, "id-3"));
        log.Add(Entry(LineReason.HttpRefused, 4, "farm-c", 4, "id-4"));
        return log;
    }

    [Fact]
    public void REQ_BT_045_Size_lives_in_one_constant_of_5000()
    {
        Assert.Equal(5000, LineLog.Capacity);
    }

    [Fact]
    public void REQ_BT_045_Query_returns_newest_first()
    {
        var page = Filled().Query(new LogQuery());

        Assert.Equal([4L, 3, 2, 1, 1], page.Entries.Select(e => e.BatchNumber));
    }

    [Fact]
    public void REQ_BT_045_Buffer_wraps_keeps_newest_and_reports_what_rolled_out()
    {
        var log = new LineLog();
        for (var i = 1; i <= LineLog.Capacity + 7; i++) log.Add(Entry(number: i));

        var page = log.Query(new LogQuery(Limit: LineLog.Capacity));

        Assert.Equal(LineLog.Capacity, page.Matched);
        Assert.Equal(7, page.Evicted);
        Assert.Equal(LineLog.Capacity + 7, page.Entries[0].BatchNumber);
        Assert.Equal(8, page.Entries[^1].BatchNumber);
    }

    [Fact]
    public void REQ_BT_045_Buffer_exactly_full_rolls_nothing_out()
    {
        var log = new LineLog();
        for (var i = 0; i < LineLog.Capacity; i++) log.Add(Entry());

        var page = log.Query(new LogQuery());

        Assert.Equal(LineLog.Capacity, page.Matched);
        Assert.Equal(0, page.Evicted);
    }

    [Fact]
    public void REQ_BT_045_Errors_equal_the_sum_per_reason_after_the_buffer_wrapped()
    {
        var log = new LineLog();
        for (var i = 0; i < LineLog.Capacity + 100; i++) log.Add(Entry(LineReason.Errors[i % LineReason.Errors.Count]));
        for (var i = 0; i < 10; i++) log.Add(Entry(LineReason.Closed));

        var counts = log.Counts();

        Assert.Equal(LineLog.Capacity + 100, counts.Errors);
        Assert.Equal(counts.Errors, counts.ErrorsByReason.Values.Sum());
        Assert.Equal(LineReason.Errors.Order(), counts.ErrorsByReason.Keys.Order());
        Assert.Equal(10, counts.Closed);
    }

    [Fact]
    public void REQ_BT_045_Reasons_with_no_errors_still_appear_at_zero()
    {
        Assert.All(new LineLog().Counts().ErrorsByReason.Values, n => Assert.Equal(0, n));
    }

    [Fact]
    public void REQ_BT_045_Limit_reports_what_it_dropped_and_counts_cover_all_matched()
    {
        var page = Filled().Query(new LogQuery(Level: LineLevel.Error, Limit: 1));

        Assert.Equal(3, page.Matched);
        Assert.Single(page.Entries);
        Assert.Equal(2, page.DroppedByLimit);
        Assert.Equal(2, page.ByReason[LineReason.HttpRefused]);
        Assert.Equal(1, page.ByReason[LineReason.EventTimeout]);
    }

    [Fact]
    public void REQ_BT_045_Default_limit_applies_and_reports_the_rest()
    {
        var log = new LineLog();
        for (var i = 0; i < LineLog.DefaultLimit + 3; i++) log.Add(Entry());

        var page = log.Query(new LogQuery());

        Assert.Equal(LineLog.DefaultLimit, page.Entries.Count);
        Assert.Equal(3, page.DroppedByLimit);
    }

    public static TheoryData<LogQuery, long[]> Filters() => new()
    {
        { new LogQuery(Level: LineLevel.Info), [3, 1] },
        { new LogQuery(Level: LineLevel.Error), [4, 2, 1] },
        { new LogQuery(Reason: LineReason.EventTimeout), [2] },
        { new LogQuery(Farm: "farm-b"), [3, 2] },
        { new LogQuery(Batch: "1"), [1, 1] },
        { new LogQuery(Batch: "id-4"), [4] },
        { new LogQuery(From: Start.AddMinutes(3)), [4, 3] },
        { new LogQuery(To: Start.AddMinutes(1)), [1, 1] },
        { new LogQuery(From: Start.AddMinutes(1), To: Start.AddMinutes(1)), [1] },
        { new LogQuery(Level: LineLevel.Error, Farm: "farm-b"), [2] },
        { new LogQuery(Level: LineLevel.Info, Reason: LineReason.HttpRefused), [] },
        { new LogQuery(Reason: LineReason.HttpRefused, Farm: "farm-c", Batch: "4", From: Start, To: Start.AddHours(1)), [4] },
        { new LogQuery(Farm: "farm-unknown"), [] },
        { new LogQuery(Batch: "999"), [] }
    };

    [Theory]
    [MemberData(nameof(Filters))]
    public void REQ_BT_045_Filter_combinations(LogQuery query, long[] expected)
    {
        var page = Filled().Query(query);

        Assert.Equal(expected, page.Entries.Select(e => e.BatchNumber));
        Assert.Equal(expected.Length, page.Matched);
    }

    public static TheoryData<LogQuery> BadQueries() => new()
    {
        new LogQuery(Level: "warn"),
        new LogQuery(Level: "ERROR"),
        new LogQuery(Reason: "nope"),
        new LogQuery(Limit: 0),
        new LogQuery(Limit: LineLog.Capacity + 1),
        new LogQuery(From: Start.AddDays(1), To: Start)
    };

    [Theory]
    [MemberData(nameof(BadQueries))]
    public void REQ_BT_045_Bad_query_is_named(LogQuery query)
    {
        Assert.NotNull(LineLog.Problem(query));
    }

    [Fact]
    public void REQ_BT_045_Good_query_bounds_are_accepted()
    {
        Assert.Null(LineLog.Problem(new LogQuery(Limit: 1)));
        Assert.Null(LineLog.Problem(new LogQuery(Limit: LineLog.Capacity, From: Start, To: Start)));
    }

    [Fact]
    public async Task REQ_BT_045_Concurrent_writers_keep_the_totals_exact()
    {
        var log = new LineLog();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++) log.Add(Entry());
        })));

        Assert.Equal(16000, log.Counts().Errors);
        Assert.Equal(16000 - LineLog.Capacity, log.Query(new LogQuery()).Evicted);
    }

    [Fact]
    public void REQ_BT_052_The_log_filters_by_the_exact_id()
    {
        var log = new LineLog();
        log.Add(Entry() with { CorrelationId = "abc-1" });
        log.Add(Entry() with { CorrelationId = "abc-2" });

        Assert.Equal(1, log.Query(new LogQuery(CorrelationId: "  abc-1 ")).Matched);
        Assert.Equal(0, log.Query(new LogQuery(CorrelationId: "abc")).Matched);
        Assert.Equal(2, log.Query(new LogQuery(CorrelationId: " ")).Matched);
    }
}
