using System.Net;
using BatchTracker.Line;

namespace BatchTracker.UnitTests;

/// <summary>The Line waits on an event by reading its batch, and those reads load the store the event is waiting on.</summary>
public sealed class LinePollTests : LineFailureTest
{
    private int reads;

    private Func<HttpRequestMessage, Task<HttpResponseMessage>> AlwaysPending() => AfterCreate(_ =>
    {
        Interlocked.Increment(ref reads);
        return Json(HttpStatusCode.OK, NewBatch(), "\"e\"");
    });

    [Fact]
    public async Task REQ_BT_045_A_long_wait_reads_the_batch_less_and_less_often()
    {
        // At a steady 10 ms this wait is over 60 reads; doubling makes it 8 at most.
        var engine = Engine(AlwaysPending(), new LineOptions
        {
            EventTimeout = TimeSpan.FromMilliseconds(600),
            PollDelay = TimeSpan.FromMilliseconds(10),
            Roll = () => 0.5
        });

        await engine.RunBatchAsync(1, CancellationToken.None);

        Assert.InRange(reads, 2, 8);
    }

    [Fact]
    public async Task REQ_BT_045_The_wait_between_reads_stops_growing_at_its_limit()
    {
        var engine = Engine(AlwaysPending(), new LineOptions
        {
            EventTimeout = TimeSpan.FromMilliseconds(600),
            PollDelay = TimeSpan.FromMilliseconds(10),
            MaxPollDelay = TimeSpan.FromMilliseconds(40),
            Roll = () => 0.5
        });

        await engine.RunBatchAsync(1, CancellationToken.None);

        // Without the limit this is the 8 above; a timer on a busy machine may run late, so the floor stays low.
        Assert.True(reads >= 10, $"{reads} reads");
    }

    [Fact]
    public void REQ_BT_045_Reads_wait_200_ms_at_first_and_5_s_at_most()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(200), new LineOptions().PollDelay);
        Assert.Equal(TimeSpan.FromSeconds(5), new LineOptions().MaxPollDelay);
    }
}
