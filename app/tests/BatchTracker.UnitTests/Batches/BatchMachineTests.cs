using BatchTracker.Domain;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

public class BatchMachineTests
{
    /// <summary>Written out by hand: taken from the machine it would prove nothing.</summary>
    private static readonly HashSet<(BatchState, BatchState)> LegalAdvances =
    [
        (BatchState.Created, BatchState.Dose1),
        (BatchState.Dose1, BatchState.Dose2),
        (BatchState.Inspected, BatchState.Shipped),
        (BatchState.Shipped, BatchState.Closed)
    ];

    public static TheoryData<BatchState, BatchState> AllPairs()
    {
        var data = new TheoryData<BatchState, BatchState>();
        foreach (var from in AllStates)
            foreach (var to in AllStates)
                data.Add(from, to);
        return data;
    }

    public static TheoryData<BatchState> States() => new(AllStates);

    [Fact]
    public void REQ_BT_001_Batch_walks_Created_to_Closed()
    {
        var batch = HashChain.Seal(BatchMachine.Create("b-1", "farm-a", "Vaccine", StampAt()), "r0");
        Assert.Equal(BatchState.Created, batch.State);

        batch = HashChain.Seal(BatchMachine.Advance(batch, BatchState.Dose1, StampAt(1)).Batch!, "r1");
        batch = HashChain.Seal(BatchMachine.Advance(batch, BatchState.Dose2, StampAt(2)).Batch!, "r2");
        batch = HashChain.Seal(BatchMachine.Inspect(batch, true, null, StampAt(3)).Batch!, "r3");
        batch = HashChain.Seal(BatchMachine.Advance(batch, BatchState.Shipped, StampAt(4)).Batch!, "r4");
        batch = HashChain.Seal(BatchMachine.Advance(batch, BatchState.Closed, StampAt(5)).Batch!, "r5");

        Assert.Equal(BatchState.Closed, batch.State);
        Assert.Equal(
            [BatchState.Created, BatchState.Dose1, BatchState.Dose2, BatchState.Inspected, BatchState.Shipped, BatchState.Closed],
            batch.Steps.Select(s => s.State));
    }

    [Fact]
    public void REQ_BT_001_Each_step_opens_one_pending_event_named_by_batch_and_state()
    {
        var created = BatchMachine.Create("b-1", "farm-a", "Vaccine", StampAt());

        Assert.Equal("b-1:Created", created.Pending!.EventId);
        Assert.Equal(PendingStatus.Pending, created.Pending.Status);
        Assert.Equal(created.Steps[0].TraceId, created.Pending.TraceId);
        Assert.Equal("olivia", created.Steps[0].By);
        Assert.Equal("", created.Steps[0].RecordId);
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void REQ_BT_002_Advance_is_legal_only_for_the_next_state(BatchState from, BatchState to)
    {
        var batch = In(from);
        var move = BatchMachine.Advance(batch, to, StampAt(9));

        if (LegalAdvances.Contains((from, to)))
        {
            Assert.Equal(to, move.Batch!.State);
            Assert.Equal(batch.Steps.Count + 1, move.Batch.Steps.Count);
        }
        else
        {
            Assert.Null(move.Batch);
            Assert.Equal(Refusal.Illegal, move.Refusal);
        }
    }

    [Theory]
    [MemberData(nameof(States))]
    public void REQ_BT_002_Inspect_is_legal_only_at_Dose2(BatchState from)
    {
        var passed = BatchMachine.Inspect(In(from), true, null, StampAt(9));
        var failed = BatchMachine.Inspect(In(from), false, "Bad", StampAt(9));

        if (from == BatchState.Dose2)
        {
            Assert.Equal(BatchState.Inspected, passed.Batch!.State);
            Assert.Equal(BatchState.Rejected, failed.Batch!.State);
        }
        else
        {
            Assert.Equal(Refusal.Illegal, passed.Refusal);
            Assert.Equal(Refusal.Illegal, failed.Refusal);
        }
    }

    [Theory]
    [InlineData(BatchState.Closed)]
    [InlineData(BatchState.Rejected)]
    public void REQ_BT_002_Final_states_accept_nothing(BatchState final)
    {
        foreach (var to in AllStates)
            Assert.Equal(Refusal.Illegal, BatchMachine.Advance(In(final), to, StampAt(9)).Refusal);
        Assert.Equal(Refusal.Illegal, BatchMachine.Inspect(In(final), true, null, StampAt(9)).Refusal);
    }

    [Fact]
    public void REQ_BT_003_Failed_inspection_goes_to_Rejected_and_keeps_the_reason()
    {
        var move = BatchMachine.Inspect(In(BatchState.Dose2), false, "  Cold chain broken ", StampAt(9));

        Assert.Equal(BatchState.Rejected, move.Batch!.State);
        Assert.Equal("Cold chain broken", move.Batch.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void REQ_BT_003_Failed_inspection_without_a_real_reason_is_refused(string? reason)
    {
        var move = BatchMachine.Inspect(In(BatchState.Dose2), false, reason, StampAt(9));

        Assert.Null(move.Batch);
        Assert.Equal(Refusal.ReasonRequired, move.Refusal);
    }

    [Theory]
    [InlineData(PendingStatus.Pending)]
    [InlineData(PendingStatus.Sent)]
    [InlineData(PendingStatus.Dead)]
    public void REQ_BT_008_Advance_and_inspect_are_refused_while_an_event_exists(string status)
    {
        var pending = new PendingEvent { EventId = "e", TraceId = "t", Status = status };
        var created = In(BatchState.Created) with { Pending = pending };
        var dose2 = In(BatchState.Dose2) with { Pending = pending };

        Assert.Equal(Refusal.EventInFlight, BatchMachine.Advance(created, BatchState.Dose1, StampAt(9)).Refusal);
        Assert.Equal(Refusal.EventInFlight, BatchMachine.Inspect(dose2, true, null, StampAt(9)).Refusal);
    }

    [Theory]
    [MemberData(nameof(States))]
    public void REQ_BT_008_Advance_never_reaches_Inspected(BatchState from)
    {
        Assert.Equal(Refusal.Illegal, BatchMachine.Advance(In(from), BatchState.Inspected, StampAt(9)).Refusal);
    }

    [Fact]
    public void REQ_BT_008_Dead_event_marks_the_batch_blocked()
    {
        var pending = new PendingEvent { EventId = "e", TraceId = "t", Status = PendingStatus.Dead };

        Assert.True((In(BatchState.Dose1) with { Pending = pending }).IsBlocked());
        Assert.False((In(BatchState.Dose1) with { Pending = pending with { Status = PendingStatus.Sent } }).IsBlocked());
        Assert.False(In(BatchState.Dose1).IsBlocked());
    }
}
