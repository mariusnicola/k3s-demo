using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

public class PendingCountTests
{
    private readonly FakeStore store = new();
    private readonly DispatcherStatus status = new();

    private PendingCount Count() => new(store, status);

    private void Row(string id, string status) =>
        store.Put(In(BatchState.Created) with { Id = id, Pending = new PendingEvent { EventId = id, TraceId = "t", Status = status } });

    [Fact]
    public async Task REQ_BT_059_The_leader_counts_pending_and_sent_and_not_dead()
    {
        Row("a", PendingStatus.Pending);
        Row("b", PendingStatus.Sent);
        Row("c", PendingStatus.Dead);
        status.TermStarted();

        await Count().ReadAsync(default);

        Assert.Equal(2, status.PendingNow);
    }

    [Fact]
    public async Task REQ_BT_059_A_standby_reports_nothing_so_nothing_reads_as_zero()
    {
        await Count().ReadAsync(default);

        Assert.Null(status.PendingNow);
    }

    [Fact]
    public async Task REQ_BT_059_A_term_that_ended_clears_the_figure_on_the_next_read()
    {
        status.TermStarted();
        await Count().ReadAsync(default);
        status.TermEnded();

        await Count().ReadAsync(default);

        Assert.Null(status.PendingNow);
    }

    [Fact]
    public async Task REQ_BT_059_A_term_lost_during_the_count_leaves_no_figure()
    {
        status.TermStarted();
        store.WhileCounting = status.TermEnded;

        await Count().ReadAsync(default);

        Assert.Null(status.PendingNow);
    }

    [Fact]
    public async Task REQ_BT_059_A_failed_count_is_unknown_and_never_the_last_number()
    {
        status.TermStarted();
        await Count().ReadAsync(default);

        store.CountFails = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Count().ReadAsync(default));
        Assert.Null(status.PendingNow);
    }
}
