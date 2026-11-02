using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace BatchTracker.UnitTests;

public class PrometheusTextTests
{
    private static MeterCounters Counters(params string[] names) => new(names, ["farm-a", "farm-b"]);

    [Fact]
    public void REQ_BT_046_Every_series_starts_at_zero_so_the_first_rise_is_seen()
    {
        using var counters = Counters("bt_x_total");

        var text = counters.Text();

        Assert.Contains("# TYPE bt_x_total counter\n", text);
        Assert.Contains("bt_x_total{farm=\"farm-a\"} 0\n", text);
        Assert.Contains("bt_x_total{farm=\"farm-b\"} 0\n", text);
    }

    [Fact]
    public void REQ_BT_046_A_count_rises_only_its_own_farm()
    {
        using var counters = Counters("bt_x_total");

        counters.Count("bt_x_total", "farm-a");
        counters.Count("bt_x_total", "farm-a");

        var text = counters.Text();
        Assert.Contains("bt_x_total{farm=\"farm-a\"} 2\n", text);
        Assert.Contains("bt_x_total{farm=\"farm-b\"} 0\n", text);
    }

    [Fact]
    public void REQ_BT_046_Two_hosts_in_one_process_keep_separate_counts()
    {
        using var first = Counters("bt_x_total");
        using var second = Counters("bt_x_total");

        first.Count("bt_x_total", "farm-a");

        Assert.Contains("bt_x_total{farm=\"farm-a\"} 0\n", second.Text());
    }

    [Fact]
    public void REQ_BT_046_A_name_this_service_does_not_export_is_refused()
    {
        using var counters = Counters("bt_x_total");

        Assert.Throws<ArgumentException>(() => counters.Count("bt_other_total", "farm-a"));
    }

    [Fact]
    public void REQ_BT_046_A_farm_outside_the_known_list_adds_no_label_value()
    {
        using var counters = Counters("bt_x_total");

        counters.Count("bt_x_total", "farm\"\nevil");

        Assert.DoesNotContain("evil", counters.Text());
    }

    private const string Gauge = "bt_worker_work_in_flight";

    private static MeterCounters Working() => new(["bt_x_total"], ["farm-a"], Gauge);

    private static ValueTask<object?> Run(WorkInFlight filter, EndpointFilterDelegate next) =>
        filter.InvokeAsync(EndpointFilterInvocationContext.Create(new DefaultHttpContext()), next);

    [Fact]
    public void REQ_BT_026_The_gauge_is_written_from_the_start_at_zero()
    {
        using var counters = Working();

        Assert.Contains($"# TYPE {Gauge} gauge\n{Gauge} 0\n", counters.Text());
    }

    [Fact]
    public void REQ_BT_026_A_service_with_no_gauge_writes_none()
    {
        using var counters = Counters("bt_x_total");

        Assert.DoesNotContain(Gauge, counters.Text());
    }

    [Fact]
    public async Task REQ_BT_026_Work_counts_while_it_runs_and_not_after()
    {
        using var counters = Working();
        var seen = "";

        await Run(new WorkInFlight(counters), _ =>
        {
            seen = counters.Text();
            return ValueTask.FromResult<object?>(null);
        });

        Assert.Contains($"{Gauge} 1\n", seen);
        Assert.Contains($"{Gauge} 0\n", counters.Text());
    }

    [Fact]
    public async Task REQ_BT_026_Two_at_once_read_two()
    {
        using var counters = Working();
        var filter = new WorkInFlight(counters);
        var seen = "";

        await Run(filter, _ => Run(filter, _ =>
        {
            seen = counters.Text();
            return ValueTask.FromResult<object?>(null);
        }));

        Assert.Contains($"{Gauge} 2\n", seen);
    }

    [Fact]
    public async Task REQ_BT_026_Work_that_throws_stops_counting()
    {
        using var counters = Working();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new WorkInFlight(counters), _ => throw new InvalidOperationException()));

        Assert.Contains($"{Gauge} 0\n", counters.Text());
    }

    [Fact]
    public void REQ_BT_046_Names_are_the_published_set_by_service()
    {
        Assert.Equal(
            ["bt_batches_closed_total", "bt_batches_created_total", "bt_batches_rejected_total", "bt_batches_shipped_total",
             "bt_bundle_item_failures_total", "bt_bundle_items_total", "bt_dose1_total", "bt_dose2_total", "bt_events_created_total", "bt_events_replayed_total",
             "bt_inspections_failed_total", "bt_inspections_passed_total"],
            MetricNames.Api.Order(StringComparer.Ordinal));
        Assert.Equal(["bt_events_passed_total"], MetricNames.Worker);
        Assert.Equal(["bt_events_dead_lettered_total"], MetricNames.Dispatcher);
    }
}
