namespace BatchTracker.Application;

public static class MetricNames
{
    public const string EventsCreated = "bt_events_created_total";
    public const string EventsPassed = "bt_events_passed_total";
    public const string EventsDeadLettered = "bt_events_dead_lettered_total";
    public const string EventsReplayed = "bt_events_replayed_total";
    public const string BatchesCreated = "bt_batches_created_total";
    public const string Dose1 = "bt_dose1_total";
    public const string Dose2 = "bt_dose2_total";
    public const string InspectionsPassed = "bt_inspections_passed_total";
    public const string InspectionsFailed = "bt_inspections_failed_total";
    public const string BatchesShipped = "bt_batches_shipped_total";
    public const string BatchesClosed = "bt_batches_closed_total";
    public const string BatchesRejected = "bt_batches_rejected_total";
    public const string WorkerWorkInFlight = "bt_worker_work_in_flight";
    public const string StageSeconds = "bt_stage_seconds";
    public const string EventsPending = "bt_events_pending";
    public const string BundleItems = "bt_bundle_items_total";
    public const string BundleItemFailures = "bt_bundle_item_failures_total";

    public static readonly IReadOnlyList<string> Api =
    [
        EventsCreated, EventsReplayed, BatchesCreated, Dose1, Dose2, InspectionsPassed,
        InspectionsFailed, BatchesShipped, BatchesClosed, BatchesRejected, BundleItems, BundleItemFailures
    ];

    public static readonly IReadOnlyList<string> Worker = [EventsPassed];
    public static readonly IReadOnlyList<string> Dispatcher = [EventsDeadLettered];

    public static readonly IReadOnlyList<string> ApiStages = [Stages.Api, Stages.Store, Stages.Bundle];
    public static readonly IReadOnlyList<string> WorkerStages = [Stages.Store, Stages.Queue, Stages.Worker];
    public static readonly IReadOnlyList<string> DispatcherStages = [Stages.Store];
}

/// <summary>The values of the `stage` label. A stage is a fixed list, so the label cannot grow the series.</summary>
public static class Stages
{
    public const string Api = "api";
    public const string Store = "store";
    public const string Queue = "queue";
    public const string Worker = "worker";
    public const string Bundle = "bundle";
}

public interface IBatchCounters
{
    void Count(string name, string farm);

    /// <summary>Failures are timed too: a stage that only records successes reads faster than it is.</summary>
    void Time(string stage, double seconds);
}

/// <summary>Null when this pod has no figure, so the series goes away and never reads as zero.</summary>
public interface IPendingNow
{
    long? PendingNow { get; }
}
