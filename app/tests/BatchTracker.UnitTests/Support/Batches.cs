using BatchTracker.Domain;

namespace BatchTracker.UnitTests;

public static class Batches
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    public static readonly BatchState[] AllStates = Enum.GetValues<BatchState>();

    public static Stamp StampAt(int step = 0) => new("olivia", $"trace-{step}", Start.AddMinutes(step));

    /// <summary>Walks the real machine so a test batch can only be one the machine can produce.</summary>
    public static Batch In(BatchState state, string id = "b-1")
    {
        var batch = Seal(BatchMachine.Create(id, "farm-a", "Vaccine", StampAt()), 0);
        var step = 1;
        foreach (var next in PathTo(state))
        {
            var move = next switch
            {
                BatchState.Inspected => BatchMachine.Inspect(batch, true, null, StampAt(step)),
                BatchState.Rejected => BatchMachine.Inspect(batch, false, "Failed check", StampAt(step)),
                _ => BatchMachine.Advance(batch, next, StampAt(step))
            };
            batch = Seal(move.Batch!, step++);
        }
        return batch;
    }

    private static Batch Seal(Batch batch, int step) => HashChain.Seal(batch, $"record-{step}");

    private static BatchState[] PathTo(BatchState state) => state switch
    {
        BatchState.Created => [],
        BatchState.Dose1 => [BatchState.Dose1],
        BatchState.Dose2 => [BatchState.Dose1, BatchState.Dose2],
        BatchState.Inspected => [BatchState.Dose1, BatchState.Dose2, BatchState.Inspected],
        BatchState.Shipped => [BatchState.Dose1, BatchState.Dose2, BatchState.Inspected, BatchState.Shipped],
        BatchState.Closed => [BatchState.Dose1, BatchState.Dose2, BatchState.Inspected, BatchState.Shipped, BatchState.Closed],
        _ => [BatchState.Dose1, BatchState.Dose2, BatchState.Rejected]
    };
}
