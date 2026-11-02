using BatchTracker.Domain;

namespace BatchTracker.Application;

/// <summary>What a saved write tells the outside: its counters and its hops.</summary>
public sealed class BatchWriteNews(IHopReporter hops, TimeProvider time, IBatchCounters counters)
{
    public void Created(Batch batch)
    {
        counters.Count(MetricNames.BatchesCreated, batch.FarmId);
        counters.Count(MetricNames.EventsCreated, batch.FarmId);
        Report(batch);
    }

    public void Moved(Batch batch)
    {
        counters.Count(MetricNames.EventsCreated, batch.FarmId);
        var moved = batch.State switch
        {
            BatchState.Dose1 => MetricNames.Dose1,
            BatchState.Dose2 => MetricNames.Dose2,
            BatchState.Inspected => MetricNames.InspectionsPassed,
            BatchState.Shipped => MetricNames.BatchesShipped,
            BatchState.Closed => MetricNames.BatchesClosed,
            _ => MetricNames.InspectionsFailed
        };
        counters.Count(moved, batch.FarmId);
        if (batch.State == BatchState.Rejected) counters.Count(MetricNames.BatchesRejected, batch.FarmId);
        Report(batch);
    }

    private void Report(Batch batch) =>
        hops.Report(new Hop(batch.Pending!.TraceId, batch.Id, batch.FarmId, HopNames.Api, batch.State.ToString(), time.GetUtcNow(), true));
}
