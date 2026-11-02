using Xunit;

public class SweepRuleTests
{
    private const string Pod = "dispatcher-node2";

    [Theory]
    [InlineData(0.2)]
    [InlineData(4)]
    [InlineData(60)]
    public void REQ_BT_019_A_leader_that_swept_inside_the_limit_has_no_problem(double seconds)
    {
        Assert.Null(SweepRule.Problem(Pod, new DispatcherState(true, true, seconds)));
    }

    [Fact]
    public void REQ_BT_019_The_lease_held_with_no_term_running_is_named()
    {
        var problem = SweepRule.Problem(Pod, new DispatcherState(false, false, null));

        Assert.Equal($"{Pod} holds the lease but is not leading, so nothing sweeps", problem);
    }

    [Fact]
    public void REQ_BT_019_A_leader_past_the_limit_says_how_long()
    {
        Assert.Equal($"{Pod} last swept 61 s ago", SweepRule.Problem(Pod, new DispatcherState(true, true, 61.4)));
    }

    [Fact]
    public void REQ_BT_019_A_leader_with_no_figure_is_a_failure()
    {
        Assert.Equal($"{Pod} reports no sweep", SweepRule.Problem(Pod, new DispatcherState(true, true, null)));
    }
}
