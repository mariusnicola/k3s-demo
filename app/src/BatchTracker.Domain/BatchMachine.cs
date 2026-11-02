namespace BatchTracker.Domain;

public sealed record Stamp(string By, string TraceId, DateTimeOffset At, string? CorrelationId = null);

public enum Refusal { Illegal, EventInFlight, ReasonRequired }

public sealed record Move(Batch? Batch, Refusal? Refusal, string? Message)
{
    public static Move To(Batch batch) => new(batch, null, null);
    public static Move Refuse(Refusal refusal, string message) => new(null, refusal, message);
}

public static class BatchMachine
{
    public static string EventIdFor(string batchId, BatchState state) => $"{batchId}:{state}";

    public static Batch Create(string id, string farmId, string product, Stamp stamp)
    {
        var batch = new Batch
        {
            Id = id,
            FarmId = farmId,
            Product = product,
            State = BatchState.Created,
            CreatedAt = stamp.At
        };
        return Enter(batch, BatchState.Created, stamp);
    }

    public static Move Advance(Batch batch, BatchState to, Stamp stamp)
    {
        if (batch.Pending is not null) return InFlight(batch);
        return IsAdvance(batch.State, to)
            ? Move.To(Enter(batch, to, stamp))
            : Move.Refuse(Refusal.Illegal, $"{batch.State} cannot advance to {to}.");
    }

    public static Move Inspect(Batch batch, bool passed, string? reason, Stamp stamp)
    {
        if (!passed && string.IsNullOrWhiteSpace(reason))
            return Move.Refuse(Refusal.ReasonRequired, "A failed inspection needs a reason.");
        if (batch.Pending is not null) return InFlight(batch);
        if (batch.State != BatchState.Dose2)
            return Move.Refuse(Refusal.Illegal, $"{batch.State} cannot be inspected.");
        return passed
            ? Move.To(Enter(batch, BatchState.Inspected, stamp))
            : Move.To(Enter(batch, BatchState.Rejected, stamp) with { Reason = reason!.Trim() });
    }

    /// <summary>Inspected and Rejected are reached only by inspect, so they are not advances.</summary>
    public static bool IsAdvance(BatchState from, BatchState to) => (from, to) is
        (BatchState.Created, BatchState.Dose1) or
        (BatchState.Dose1, BatchState.Dose2) or
        (BatchState.Inspected, BatchState.Shipped) or
        (BatchState.Shipped, BatchState.Closed);

    public static bool IsStep(BatchState from, BatchState to) =>
        IsAdvance(from, to) ||
        (from == BatchState.Dose2 && to is BatchState.Inspected or BatchState.Rejected);

    private static Move InFlight(Batch batch) => Move.Refuse(
        Refusal.EventInFlight,
        batch.IsBlocked() ? "Batch is blocked until replayed." : "An event for this batch is still in flight.");

    private static Batch Enter(Batch batch, BatchState state, Stamp stamp) => batch with
    {
        State = state,
        Steps = [.. batch.Steps, new Step { State = state, At = stamp.At, By = stamp.By, TraceId = stamp.TraceId }],
        Pending = new PendingEvent
        {
            EventId = EventIdFor(batch.Id, state),
            TraceId = stamp.TraceId,
            CorrelationId = stamp.CorrelationId,
            Status = PendingStatus.Pending
        }
    };
}
