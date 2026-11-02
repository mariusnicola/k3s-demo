using BatchTracker.Application.Dispatching;

namespace BatchTracker.UnitTests;

/// <summary>A loss can reach a pod that never led: a lease call failing while it stands by also ends in Lose.</summary>
public class LeadershipLossTests : DispatcherRunnerTest
{
    [Fact]
    public async Task REQ_BT_019_A_wait_taken_before_a_loss_is_answered_by_the_next_gain()
    {
        var signal = new LeadershipSignal();
        var waiting = signal.WaitForLeadershipAsync(default);

        signal.Lose();
        signal.Gain();

        var term = await waiting.WaitAsync(Limit);
        Assert.False(term.IsCancellationRequested);
    }

    [Fact]
    public async Task REQ_BT_019_A_standby_that_saw_a_lease_error_sweeps_once_it_leads()
    {
        AddPending();
        Run();

        leadership.Lose();
        leadership.Gain();

        await Until(() => bus.Sent.Count == 1, "the row was never sent");
        Assert.True(status.Leader);
    }

    [Fact]
    public async Task REQ_BT_019_A_lost_term_is_not_handed_out_again()
    {
        var signal = new LeadershipSignal();
        signal.Gain();
        var term = await signal.WaitForLeadershipAsync(default).WaitAsync(Limit);

        signal.Lose();

        Assert.True(term.IsCancellationRequested);
        Assert.False(signal.WaitForLeadershipAsync(default).IsCompleted);
    }

    [Fact]
    public async Task REQ_BT_019_Losses_in_a_row_still_leave_the_next_gain_a_live_term()
    {
        var signal = new LeadershipSignal();
        var waiting = signal.WaitForLeadershipAsync(default);

        signal.Lose();
        signal.Lose();
        signal.Gain();
        signal.Lose();
        var lost = await waiting.WaitAsync(Limit);
        signal.Gain();

        Assert.True(lost.IsCancellationRequested);
        var term = await signal.WaitForLeadershipAsync(default).WaitAsync(Limit);
        Assert.False(term.IsCancellationRequested);
    }
}
