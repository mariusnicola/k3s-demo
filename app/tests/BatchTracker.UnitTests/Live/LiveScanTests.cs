using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Live;
using Microsoft.Extensions.Logging;

namespace BatchTracker.UnitTests;

/// <summary>Live answers for every batch while holding only a bounded set of rows.</summary>
public class LiveScanTests
{
    private static readonly Caller Everyone = new("ada", [], [Caller.AllFarms]);
    private static readonly Caller FarmA = new("viewer", [], ["farm-a"]);

    private readonly HopHub hub = new(TimeProvider.System);

    private static Versioned Row(string id, BatchState state = BatchState.Created, string farm = "farm-a", params BatchState[] steps) => new(new Batch
    {
        Id = id,
        FarmId = farm,
        Product = "p",
        State = state,
        CreatedAt = DateTimeOffset.UnixEpoch,
        Version = steps.Length + 1,
        Steps = [.. steps.Select(s => new Step { State = s, At = DateTimeOffset.UnixEpoch, By = "ada", TraceId = "t" })]
    }, "e");

    /// <summary>The row as a write that created it or changed its state leaves it.</summary>
    private static Versioned Fresh(Versioned row) => row with
    {
        Batch = row.Batch with { Pending = new PendingEvent { EventId = "e", TraceId = "t", Status = PendingStatus.Pending } }
    };

    private JsonElement Snapshot(Caller caller)
    {
        Assert.True(hub.Connect(caller).Outbox.TryRead(out var frame));
        return JsonDocument.Parse(frame!).RootElement;
    }

    private int Count(BatchState state, Caller? caller = null) =>
        Snapshot(caller ?? Everyone).GetProperty("summary").GetProperty("byState").GetProperty(state.ToString()).GetInt32();

    [Fact]
    public void REQ_BT_028_The_summary_counts_batches_no_row_is_held_for()
    {
        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Closed", 27000)], []));

        Assert.Equal(27000, Count(BatchState.Closed));
    }

    [Fact]
    public void REQ_BT_028_A_caller_sees_only_the_counts_of_their_farms()
    {
        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Closed", 3), new StateCount("farm-b", "Closed", 4)], []));

        Assert.Equal(3, Count(BatchState.Closed, FarmA));
        Assert.Equal(7, Count(BatchState.Closed));
    }

    [Fact]
    public void REQ_BT_028_Dropped_rows_are_counted_from_the_counts()
    {
        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Closed", 500)], [Row("a", BatchState.Closed)]));

        Assert.Equal(500 - hub.FullRowsHeld, Snapshot(Everyone).GetProperty("batchesDropped").GetInt32());
    }

    [Fact]
    public void REQ_BT_028_A_write_moves_one_count_from_the_state_held()
    {
        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Created", 10)], [Row("a")]));

        hub.Apply(Row("a", BatchState.Dose1));

        Assert.Equal(9, Count(BatchState.Created));
        Assert.Equal(1, Count(BatchState.Dose1));
    }

    [Fact]
    public void REQ_BT_028_A_write_to_a_row_not_held_moves_from_its_previous_step()
    {
        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Dose1", 10)], []));

        hub.Apply(Fresh(Row("old", BatchState.Dose2, steps: [BatchState.Created, BatchState.Dose1, BatchState.Dose2])));

        Assert.Equal(9, Count(BatchState.Dose1));
        Assert.Equal(1, Count(BatchState.Dose2));
    }

    [Fact]
    public void REQ_BT_028_A_new_batch_adds_one_count()
    {
        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Created", 10)], []));

        hub.Apply(Fresh(Row("new", steps: [BatchState.Created])));

        Assert.Equal(11, Count(BatchState.Created));
    }

    [Fact]
    public void REQ_BT_028_The_same_write_twice_counts_once()
    {
        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Created", 10)], []));
        var row = Fresh(Row("new", steps: [BatchState.Created]));

        hub.Apply(row);
        hub.Apply(row);

        Assert.Equal(11, Count(BatchState.Created));
    }

    [Fact]
    public void REQ_BT_028_A_count_never_goes_below_zero()
    {
        hub.Apply(Fresh(Row("old", BatchState.Dose2, steps: [BatchState.Created, BatchState.Dose1, BatchState.Dose2])));

        Assert.Equal(0, Count(BatchState.Dose1));
    }

    [Fact]
    public void REQ_BT_028_Each_scan_replaces_the_counts()
    {
        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Created", 10)], []));
        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Closed", 2)], []));

        Assert.Equal(0, Count(BatchState.Created));
        Assert.Equal(2, Count(BatchState.Closed));
    }

    [Theory]
    [InlineData(PendingStatus.Sent)]
    [InlineData(null)]
    public void B_037_A_later_write_to_a_created_row_not_held_adds_nothing(string? status)
    {
        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Created", 10)], []));
        var old = Row("old", steps: [BatchState.Created]);
        var pending = status is null ? null : new PendingEvent { EventId = "e", TraceId = "t", Status = status };

        hub.Apply(old with { Batch = old.Batch with { Pending = pending } });

        Assert.Equal(10, Count(BatchState.Created));
    }

    [Theory]
    [InlineData(BatchState.Created, new[] { BatchState.Created })]
    [InlineData(BatchState.Dose2, new[] { BatchState.Created, BatchState.Dose1, BatchState.Dose2 })]
    public void B_037_A_replay_on_a_row_not_held_moves_no_count(BatchState state, BatchState[] steps)
    {
        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Dose1", 10), new StateCount("farm-a", state.ToString(), 4)], []));
        var old = Fresh(Row("old", state, steps: steps));

        hub.Apply(old with { Batch = old.Batch with { Pending = old.Batch.Pending! with { ReplayOf = "first" } } });

        Assert.Equal(10, Count(BatchState.Dose1));
        Assert.Equal(4, Count(state));
    }

    [Fact]
    public void B_027_A_flag_written_on_a_row_not_held_moves_no_count()
    {
        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Dose1", 10), new StateCount("farm-a", "Dose2", 4)], []));
        var old = Row("old", BatchState.Dose2, steps: [BatchState.Created, BatchState.Dose1, BatchState.Dose2]);

        hub.Apply(old with { Batch = old.Batch with { Flags = [new Flag("Overdue", DateTimeOffset.UnixEpoch)] } });

        Assert.Equal(10, Count(BatchState.Dose1));
        Assert.Equal(4, Count(BatchState.Dose2));
    }

    [Fact]
    public void B_027_A_row_written_during_a_scan_moves_from_where_it_was()
    {
        hub.BeginReconcile();
        hub.Apply(Fresh(Row("old", BatchState.Dose2, steps: [BatchState.Created, BatchState.Dose1, BatchState.Dose2])));

        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Dose1", 10)], []));

        Assert.Equal(9, Count(BatchState.Dose1));
        Assert.Equal(1, Count(BatchState.Dose2));
    }

    [Fact]
    public void B_027_A_batch_created_during_a_scan_adds_one()
    {
        hub.BeginReconcile();
        hub.Apply(Fresh(Row("new", steps: [BatchState.Created])));

        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Created", 10)], []));

        Assert.Equal(11, Count(BatchState.Created));
    }

    [Fact]
    public void B_027_Dropped_rows_never_read_below_zero()
    {
        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Closed", 1)], []));
        hub.Apply(Row("a", BatchState.Closed));
        hub.FinishReconcile(new LiveScan([], [Row("a", BatchState.Closed)]));

        Assert.Equal(0, Snapshot(Everyone).GetProperty("batchesDropped").GetInt32());
    }

    [Fact]
    public void B_026_A_snapshot_says_the_open_rows_were_capped()
    {
        hub.FinishReconcile(new LiveScan([], [], Capped: true));

        Assert.True(Snapshot(Everyone).GetProperty("capped").GetBoolean());
    }

    [Fact]
    public void B_026_A_client_hears_when_the_cap_starts_and_ends()
    {
        var client = hub.Connect(Everyone);
        Assert.True(client.Outbox.TryRead(out _));

        hub.FinishReconcile(new LiveScan([], [], Capped: true));
        hub.PublishReports();
        Assert.True(client.Outbox.TryRead(out var frame));
        Assert.True(JsonDocument.Parse(frame!).RootElement.GetProperty("capped").GetBoolean());

        hub.FinishReconcile(new LiveScan([], []));
        hub.PublishReports();
        Assert.True(client.Outbox.TryRead(out frame));
        Assert.False(JsonDocument.Parse(frame!).RootElement.GetProperty("capped").GetBoolean());
    }

    [Theory]
    [InlineData(BatchState.Closed)]
    [InlineData(BatchState.Rejected)]
    public void B_026_A_flag_on_a_finished_batch_stops_counting(BatchState state)
    {
        var finished = Row("a", state).Batch with { Flags = [new Flag("Overdue", DateTimeOffset.UnixEpoch)] };

        Assert.Equal(0, Reports.SummaryOf([finished]).Flagged);
        Assert.Empty(Reports.AlertsOf([finished]));
    }

    [Fact]
    public void B_026_A_flag_on_a_running_batch_counts()
    {
        var running = Row("a", BatchState.Dose1).Batch with { Flags = [new Flag("Overdue", DateTimeOffset.UnixEpoch)] };

        Assert.Equal(1, Reports.SummaryOf([running]).Flagged);
        Assert.Single(Reports.AlertsOf([running]));
    }

    [Fact]
    public void REQ_BT_028_Open_rows_still_answer_pending_and_blocked()
    {
        var blocked = Row("b") with { Batch = Row("b").Batch with { Pending = new PendingEvent { EventId = "e", TraceId = "t", Status = PendingStatus.Dead } } };
        hub.FinishReconcile(new LiveScan([new StateCount("farm-a", "Created", 900)], [blocked]));

        var summary = Snapshot(Everyone).GetProperty("summary");

        Assert.Equal(1, summary.GetProperty("blocked").GetInt32());
        Assert.Equal(0, summary.GetProperty("pending").GetInt32());
    }

    /// <summary>A store whose every scan comes back capped.</summary>
    public class CappedStore : System.Reflection.DispatchProxy
    {
        public static IBatchStore Wrap() => Create<IBatchStore, CappedStore>();

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            Task.FromResult(new LiveScan([], [], Capped: true));
    }

    /// <summary>Catches the first warning the reconciler logs.</summary>
    private sealed class Warnings : ILogger<LiveReconciler>
    {
        public TaskCompletionSource<string> First { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) First.TrySetResult(formatter(state, exception));
        }
    }

    [Fact]
    public async Task REQ_BT_028_A_capped_scan_is_a_warning_naming_the_cap()
    {
        var warnings = new Warnings();
        using var reconciler = new LiveReconciler(hub, CappedStore.Wrap(), warnings);

        await reconciler.StartAsync(default);
        var text = await warnings.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await reconciler.StopAsync(default);

        Assert.Contains(LiveScan.MaxOpen.ToString(), text);
    }
}
