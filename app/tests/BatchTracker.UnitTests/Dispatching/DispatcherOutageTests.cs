using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;

namespace BatchTracker.UnitTests;

/// <summary>One bad answer is one message's problem: only failures in a row mean the Worker is gone.</summary>
public sealed class DispatcherOutageTests : DispatcherRunnerTest
{
    public DispatcherOutageTests()
    {
        options = options with { WorkerFailuresToStop = 3, DeliveryConcurrency = 1 };
        bus.EchoSends = false;
    }

    private int GivenBack => store.Returned;

    private void AnswerFor(string failing, WorkerResult answer) => worker.Handler = (message, _) =>
        Task.FromResult(failing.Contains(message.BatchId[^1]) ? answer : WorkerResult.Done);

    [Fact]
    public void REQ_BT_021_Three_failures_in_a_row_stop_the_dispatcher()
    {
        Assert.Equal(3, new DispatcherOptions().WorkerFailuresToStop);
    }

    [Fact]
    public async Task REQ_BT_021_One_failed_delivery_stops_nothing()
    {
        AnswerFor("0", WorkerResult.Unavailable);
        Queue(5);
        Run();
        leadership.Gain();

        await Until(() => Completed == 5 && GivenBack >= 1, "the rest delivered");

        AddPending("late");
        await Until(() => bus.Sent.Any(e => e.BatchId == "late"), "the sweep still running");
        Assert.Equal(0, worker.ReadyCalls);
    }

    [Fact]
    public async Task REQ_BT_021_Failures_in_a_row_stop_receiving_and_sweeping()
    {
        worker.Ready = false;
        worker.Handler = (_, _) => Task.FromResult(WorkerResult.Unavailable);
        Queue(3);
        Run();
        leadership.Gain();

        await Until(() => worker.ReadyCalls > 0, "probing");

        AddPending("late");
        await Task.Delay(100);
        Assert.DoesNotContain(bus.Sent, e => e.BatchId == "late");
    }

    [Fact]
    public async Task REQ_BT_021_An_answer_between_failures_starts_the_count_again()
    {
        AnswerFor("0134", WorkerResult.Unavailable);
        Queue(5);
        Run();
        leadership.Gain();

        await Until(() => Completed == 5, "all five answered");

        Assert.Equal(0, worker.ReadyCalls);
    }

    [Fact]
    public async Task REQ_BT_021_A_contended_batch_is_given_back_logged_and_counts_for_nothing()
    {
        worker.Handler = (_, _) => Task.FromResult(WorkerResult.Contended);
        Queue(6);
        Run();
        leadership.Gain();

        await Until(() => GivenBack >= 6, "all six back on their rows");

        Assert.Equal(0, worker.ReadyCalls);
        await Until(() => { lock (errors) return errors.Count(step => step == "deliver") == 6; }, "each one logged");
    }

    [Fact]
    public async Task REQ_BT_021_A_failed_delivery_is_logged()
    {
        AnswerFor("0", WorkerResult.Unavailable);
        Queue(1);
        Run();
        leadership.Gain();

        await Until(() => GivenBack >= 1, "given back");

        await Until(() => { lock (errors) return errors.Contains("deliver"); }, "logged");
    }

    [Fact]
    public async Task REQ_BT_021_After_an_outage_the_count_starts_from_nothing()
    {
        worker.Handler = (_, _) => Task.FromResult(WorkerResult.Unavailable);
        Queue(3);
        Run();
        leadership.Gain();
        await Until(() => worker.ReadyCalls > 0, "outage over");
        await Until(() => Completed == 3 && !bus.Incoming.Any(), "the three settled");
        await Task.Delay(50);
        var probes = worker.ReadyCalls;

        QueueOne("one-more");

        // The count and not the status: the next sweep marks a row given back as sent again, so a late poll misses pending.
        await Until(() => Completed == 4, "the next failure given back");
        await Task.Delay(100);
        Assert.Equal(probes, worker.ReadyCalls);
    }
}
