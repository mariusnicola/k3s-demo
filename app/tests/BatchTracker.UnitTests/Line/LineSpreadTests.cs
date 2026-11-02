using System.Net;
using BatchTracker.Line;

namespace BatchTracker.UnitTests;

/// <summary>Walks that step together would read together, in waves, unless each wait is its own length.</summary>
public sealed class LineSpreadTests : LineFailureTest
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(4);

    [Theory]
    [InlineData(0.0, 2.0)]
    [InlineData(0.5, 4.0)]
    [InlineData(1.0, 6.0)]
    public void REQ_BT_045_A_wait_spreads_half_either_side_of_its_length(double roll, double seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), LineApi.Spread(Wait, roll));
    }

    [Fact]
    public void REQ_BT_045_The_spread_keeps_the_average_so_the_reads_on_the_store_do_not_grow()
    {
        var rolls = Enumerable.Range(0, 1000).Select(i => (i + 0.5) / 1000);

        Assert.Equal(Wait.TotalSeconds, rolls.Average(roll => LineApi.Spread(Wait, roll).TotalSeconds), 3);
    }

    [Fact]
    public void REQ_BT_045_By_default_each_wait_rolls_its_own_length()
    {
        var options = new LineOptions();

        var waits = Enumerable.Range(0, 100).Select(_ => LineApi.Spread(Wait, options.Roll())).Distinct().Count();

        Assert.True(waits > 50, $"{waits} different waits in 100");
    }

    [Fact]
    public async Task REQ_BT_045_The_roll_reaches_the_real_wait_between_reads()
    {
        Assert.True(await ReadsWith(0) > await ReadsWith(1));
    }

    private async Task<int> ReadsWith(double roll)
    {
        var reads = 0;
        var engine = Engine(AfterCreate(_ =>
        {
            Interlocked.Increment(ref reads);
            return Json(HttpStatusCode.OK, NewBatch(), "\"e\"");
        }), new LineOptions
        {
            EventTimeout = TimeSpan.FromMilliseconds(600),
            PollDelay = TimeSpan.FromMilliseconds(40),
            MaxPollDelay = TimeSpan.FromMilliseconds(40),
            Roll = () => roll
        });

        await engine.RunBatchAsync(1, CancellationToken.None);
        return reads;
    }
}
