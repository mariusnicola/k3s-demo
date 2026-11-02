using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Live;

namespace BatchTracker.UnitTests;

/// <summary>The rows the hub holds and the reports made from them.</summary>
public class HopHubRowTests : HopHubTest
{
    [Fact]
    public void REQ_BT_028_Older_row_arriving_late_changes_nothing()
    {
        var hub = new HopHub(TimeProvider.System);
        hub.Apply(Row("a", 3, state: BatchState.Dose1));

        hub.Apply(Row("a", 2));

        var row = Snapshot(hub, Everyone).GetProperty("batches")[0];
        Assert.Equal(3, row.GetProperty("batch").GetProperty("version").GetInt64());
    }

    [Fact]
    public void REQ_BT_028_Reconcile_corrects_a_change_lost_on_the_way()
    {
        var hub = new HopHub(TimeProvider.System);
        hub.Apply(Row("a", 1));

        hub.BeginReconcile();
        var missing = hub.FinishReconcile(Scan.Of([Row("a", 2, state: BatchState.Dose1), Row("b", 1)]));

        var snapshot = Snapshot(hub, Everyone);
        Assert.Equal(1, Created(snapshot));
        Assert.Equal(1, snapshot.GetProperty("summary").GetProperty("byState").GetProperty("Dose1").GetInt32());
        Assert.Equal(["a", "b"], missing.Select(b => b.Id).Order());
    }

    [Fact]
    public void REQ_BT_028_Reconcile_removes_a_row_the_store_no_longer_holds()
    {
        var hub = new HopHub(TimeProvider.System);
        hub.Apply(Row("a", 1));

        hub.BeginReconcile();
        hub.FinishReconcile(Scan.Of([]));

        var snapshot = Snapshot(hub, Everyone);
        Assert.Equal(0, Created(snapshot));
        Assert.Equal(0, snapshot.GetProperty("batches").GetArrayLength());
    }

    [Fact]
    public void REQ_BT_028_Write_during_a_scan_survives_the_scan()
    {
        var hub = new HopHub(TimeProvider.System);
        hub.Apply(Row("a", 1));

        hub.BeginReconcile();
        hub.Apply(Row("a", 2, state: BatchState.Dose1));
        hub.Apply(New(Row("new", 1)));
        hub.FinishReconcile(Scan.Of([Row("a", 1)]));

        var snapshot = Snapshot(hub, Everyone);
        Assert.Equal(1, Created(snapshot));
        Assert.Equal(1, snapshot.GetProperty("summary").GetProperty("byState").GetProperty("Dose1").GetInt32());
    }

    [Fact]
    public void REQ_BT_028_Reconcile_time_shows_in_the_snapshot()
    {
        var hub = new HopHub(TimeProvider.System);
        Assert.Equal(JsonValueKind.Null, Snapshot(hub, Everyone).GetProperty("reconciledAt").ValueKind);

        hub.BeginReconcile();
        hub.FinishReconcile(Scan.Of([]));

        Assert.Equal(JsonValueKind.String, Snapshot(hub, Everyone).GetProperty("reconciledAt").ValueKind);
    }

    [Fact]
    public void REQ_BT_028_Snapshot_keeps_the_newest_200_and_counts_the_rest()
    {
        var hub = new HopHub(TimeProvider.System);
        for (var i = 0; i < HopHub.RowsPerSnapshot + 7; i++)
            hub.Apply(New(Row($"r{i}", 1, createdAt: DateTimeOffset.UnixEpoch.AddMinutes(i))));

        var snapshot = Snapshot(hub, Everyone);

        Assert.Equal(HopHub.RowsPerSnapshot, snapshot.GetProperty("batches").GetArrayLength());
        Assert.Equal(7, snapshot.GetProperty("batchesDropped").GetInt32());
        Assert.Equal($"r{HopHub.RowsPerSnapshot + 6}", snapshot.GetProperty("batches")[0].GetProperty("batch").GetProperty("id").GetString());
    }

    [Fact]
    public void REQ_BT_028_Reports_are_held_for_the_row_without_its_steps_and_the_frame_keeps_them()
    {
        var hub = new HopHub(TimeProvider.System);
        var client = Connected(hub, Everyone);
        var step = new Step { State = BatchState.Created, At = DateTimeOffset.UnixEpoch, By = "ada", TraceId = "t" };
        var row = Row("a", 1);

        hub.Apply(row with { Batch = row.Batch with { Steps = [step] } });

        client.Outbox.TryRead(out var frame);
        Assert.Equal(1, JsonDocument.Parse(frame!).RootElement.GetProperty("batch").GetProperty("steps").GetArrayLength());
        Assert.Equal(1, Snapshot(hub, Everyone).GetProperty("batches")[0].GetProperty("batch").GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public void REQ_BT_016_Summary_frame_counts_only_own_farms()
    {
        var hub = new HopHub(TimeProvider.System);
        var client = Connected(hub, FarmA);
        hub.Apply(New(Row("a", 1, "farm-a")));
        hub.Apply(New(Row("b", 1, "farm-b")));
        client.Outbox.TryRead(out _);

        hub.PublishReports();

        client.Outbox.TryRead(out var frame);
        var root = JsonDocument.Parse(frame!).RootElement;
        Assert.Equal("summary", root.GetProperty("type").GetString());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("byState").GetProperty("Created").GetInt32());
    }

    [Fact]
    public void REQ_BT_028_Reports_go_out_only_when_they_changed()
    {
        var hub = new HopHub(TimeProvider.System);
        hub.BeginReconcile();
        hub.FinishReconcile(Scan.Of([]));
        var client = Connected(hub, Everyone);

        hub.PublishReports();
        hub.BeginReconcile();
        hub.FinishReconcile(Scan.Of([]));
        hub.PublishReports();

        Assert.False(client.Outbox.TryRead(out _));
    }

    [Fact]
    public void REQ_BT_028_Full_rows_held_stay_bounded_per_farm()
    {
        var hub = new HopHub(TimeProvider.System);

        for (var i = 0; i < HopHub.RowsPerSnapshot * 3; i++)
        {
            hub.Apply(Row($"a{i}", 1, "farm-a", createdAt: DateTimeOffset.UnixEpoch.AddMinutes(i)));
            hub.Apply(Row($"b{i}", 1, "farm-b", createdAt: DateTimeOffset.UnixEpoch.AddMinutes(i)));
        }

        Assert.Equal(HopHub.RowsPerSnapshot * 2, hub.FullRowsHeld);
    }
}
