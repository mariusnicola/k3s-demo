using BatchTracker.Domain;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

public class HashChainTests
{
    private static Batch WithStep(Batch batch, int index, Func<Step, Step> change)
    {
        var steps = batch.Steps.ToList();
        steps[index] = change(steps[index]);
        return batch with { Steps = steps };
    }

    public static TheoryData<string> Fields() => new("by", "traceId", "at", "recordId", "hash", "previousHash");

    private static Step Tamper(Step step, string field) => field switch
    {
        "by" => step with { By = "mallory" },
        "traceId" => step with { TraceId = "other" },
        "at" => step with { At = step.At.AddTicks(1) },
        "recordId" => step with { RecordId = "other" },
        "hash" => step with { Hash = new string('0', 64) },
        _ => step with { PreviousHash = new string('0', 64) }
    };

    [Theory]
    [InlineData(BatchState.Closed)]
    [InlineData(BatchState.Rejected)]
    [InlineData(BatchState.Created)]
    public void REQ_BT_012_Untouched_chain_verifies(BatchState state)
    {
        Assert.True(HashChain.Verify(In(state)));
    }

    [Fact]
    public void REQ_BT_012_Each_record_links_to_the_one_before()
    {
        var steps = In(BatchState.Closed).Steps;

        Assert.Equal("", steps[0].PreviousHash);
        for (var i = 1; i < steps.Count; i++) Assert.Equal(steps[i - 1].Hash, steps[i].PreviousHash);
        Assert.Equal(steps.Count, steps.Select(s => s.Hash).Distinct().Count());
    }

    [Fact]
    public void REQ_BT_012_Newest_step_may_wait_for_its_record()
    {
        var batch = BatchMachine.Advance(In(BatchState.Created), BatchState.Dose1, StampAt(1)).Batch!;

        Assert.True(HashChain.Verify(batch));
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void REQ_BT_012_Tampered_field_is_detected_on_every_step(string field)
    {
        var batch = In(BatchState.Closed);
        for (var i = 0; i < batch.Steps.Count; i++)
            Assert.False(HashChain.Verify(WithStep(batch, i, s => Tamper(s, field))), $"step {i}");
    }

    [Fact]
    public void REQ_BT_012_Tampered_reject_reason_is_detected()
    {
        Assert.False(HashChain.Verify(In(BatchState.Rejected) with { Reason = "Nothing wrong" }));
    }

    [Fact]
    public void REQ_BT_012_Moved_to_another_farm_is_detected()
    {
        Assert.False(HashChain.Verify(In(BatchState.Closed) with { FarmId = "farm-b" }));
    }

    [Fact]
    public void REQ_BT_012_Reordered_steps_are_detected()
    {
        var batch = In(BatchState.Closed);
        var steps = batch.Steps.ToList();
        (steps[1], steps[2]) = (steps[2], steps[1]);

        Assert.False(HashChain.Verify(batch with { Steps = steps }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public void REQ_BT_012_Missing_step_is_detected(int index)
    {
        var batch = In(BatchState.Closed);
        var steps = batch.Steps.ToList();
        steps.RemoveAt(index);

        Assert.False(HashChain.Verify(batch with { Steps = steps }));
    }

    [Fact]
    public void REQ_BT_012_Empty_chain_is_detected()
    {
        Assert.False(HashChain.Verify(In(BatchState.Created) with { Steps = [] }));
    }

    [Fact]
    public void REQ_BT_012_Unsealed_step_in_the_middle_is_detected()
    {
        var batch = WithStep(In(BatchState.Closed), 2, s => s with { Hash = "", RecordId = "", PreviousHash = "" });

        Assert.False(HashChain.Verify(batch));
    }

    [Fact]
    public void REQ_BT_012_Unsealed_last_step_without_an_event_is_detected()
    {
        var batch = WithStep(In(BatchState.Closed), 5, s => s with { Hash = "", RecordId = "", PreviousHash = "" });

        Assert.False(HashChain.Verify(batch));
    }
}
