namespace BatchTracker.UnitTests;

/// <summary>The figure Monitor reads. Idle sweeps run too, so a leader whose figure climbs has stopped, rows or no rows.</summary>
public class SweepStatusTests : DispatcherRunnerTest
{
    private static readonly TimeSpan Climb = TimeSpan.FromMilliseconds(200);
    private const double FreshSeconds = 0.15;

    private async Task LeadUntilSwept()
    {
        Run();
        leadership.Gain();
        await Until(() => store.DueReads > 0 && status.SecondsSinceSweep < FreshSeconds, "no sweep");
    }

    [Fact]
    public async Task REQ_BT_019_A_pod_with_no_term_reports_no_sweep_figure()
    {
        Run();
        await Task.Delay(50);

        Assert.Null(status.SecondsSinceSweep);
    }

    [Fact]
    public async Task REQ_BT_019_A_sweeping_leader_keeps_its_figure_fresh()
    {
        await LeadUntilSwept();

        await Task.Delay(Climb);

        Assert.True(status.SecondsSinceSweep < FreshSeconds, $"{status.SecondsSinceSweep}");
    }

    [Fact]
    public async Task REQ_BT_019_A_sweep_that_fails_does_not_count_and_the_figure_climbs()
    {
        await LeadUntilSwept();

        store.ReadsFail = true;
        await Task.Delay(Climb);

        Assert.True(status.SecondsSinceSweep >= FreshSeconds, $"{status.SecondsSinceSweep}");
    }

    [Fact]
    public async Task REQ_BT_019_A_leader_that_cannot_connect_climbs_from_the_start_of_its_term()
    {
        bus.Down = true;
        Run();
        leadership.Gain();

        await Until(() => status.Leader, "never led");
        await Task.Delay(Climb);

        Assert.True(status.SecondsSinceSweep >= FreshSeconds, $"{status.SecondsSinceSweep}");
    }

    [Fact]
    public async Task REQ_BT_019_A_lost_term_clears_the_figure()
    {
        await LeadUntilSwept();

        leadership.Lose();

        await Until(() => !status.Leader, "term did not end");
        Assert.Null(status.SecondsSinceSweep);
    }
}
