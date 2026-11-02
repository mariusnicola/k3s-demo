using BatchTracker.Domain;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

public class Dose2RuleTests
{
    private const int Window = 21;
    private static readonly DateTimeOffset Dose1At = StampAt(1).At;

    public static TheoryData<BatchState> OtherStates() =>
        new(AllStates.Where(s => s != BatchState.Dose1));

    [Fact]
    public void REQ_BT_013_Dose1_past_the_window_needs_a_flag()
    {
        Assert.True(Dose2Rule.NeedsFlag(In(BatchState.Dose1), Dose1At.AddDays(Window).AddTicks(1), Window));
    }

    [Fact]
    public void REQ_BT_013_Exactly_at_the_window_is_not_overdue()
    {
        Assert.False(Dose2Rule.NeedsFlag(In(BatchState.Dose1), Dose1At.AddDays(Window), Window));
    }

    [Fact]
    public void REQ_BT_013_Flag_is_added_once()
    {
        var now = Dose1At.AddDays(Window + 1);
        var flagged = Dose2Rule.Flag(In(BatchState.Dose1), now);

        Assert.Equal([new Flag("Dose2Overdue", now)], flagged.Flags);
        Assert.False(Dose2Rule.NeedsFlag(flagged, now.AddDays(1), Window));
    }

    [Theory]
    [MemberData(nameof(OtherStates))]
    public void REQ_BT_013_Only_Dose1_batches_are_flagged(BatchState state)
    {
        Assert.False(Dose2Rule.NeedsFlag(In(state), Dose1At.AddDays(Window + 100), Window));
    }

    [Fact]
    public void REQ_BT_013_Window_comes_from_the_argument()
    {
        Assert.True(Dose2Rule.NeedsFlag(In(BatchState.Dose1), Dose1At.AddDays(4), windowDays: 3));
        Assert.False(Dose2Rule.NeedsFlag(In(BatchState.Dose1), Dose1At.AddDays(4), windowDays: 5));
    }
}
