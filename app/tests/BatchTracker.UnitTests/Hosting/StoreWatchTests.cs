using BatchTracker.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using static BatchTracker.UnitTests.Batches;

namespace BatchTracker.UnitTests;

/// <summary>Readiness reads this record, so a slow store cannot race the kubelet's own probe timeout.</summary>
public sealed class StoreWatchTests
{
    private readonly FakeStore store = new();
    private readonly Clock clock = new(Start);
    private readonly StoreWatchRules rules = new();

    private StoreWatch Watch() => new(store, clock, rules, NullLogger<StoreWatch>.Instance);

    [Fact]
    public void REQ_BT_028_Before_any_read_the_store_counts_as_not_read()
    {
        var watch = Watch();

        Assert.False(watch.Good);
        Assert.Null(watch.LastReadMs);
    }

    [Fact]
    public async Task REQ_BT_028_A_read_that_takes_four_seconds_is_good_and_says_how_long()
    {
        store.Reach = _ =>
        {
            clock.Now += TimeSpan.FromSeconds(4);
            return Task.FromResult(true);
        };
        var watch = Watch();

        await watch.CheckAsync(default);

        Assert.True(watch.Good);
        Assert.Equal(4000, watch.LastReadMs);
    }

    [Fact]
    public async Task REQ_BT_028_A_store_gone_turns_not_good_once_the_last_read_is_too_old()
    {
        var watch = Watch();
        await watch.CheckAsync(default);
        store.Reach = _ => Task.FromResult(false);

        clock.Now += rules.FreshFor;
        await watch.CheckAsync(default);
        Assert.True(watch.Good);

        clock.Now += TimeSpan.FromMilliseconds(1);
        Assert.False(watch.Good);
    }

    [Fact]
    public async Task REQ_BT_028_A_read_past_the_limit_is_cut_and_not_counted()
    {
        var limited = new StoreWatchRules { ReadLimit = TimeSpan.FromMilliseconds(20) };
        store.Reach = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return true;
        };
        var watch = new StoreWatch(store, clock, limited, NullLogger<StoreWatch>.Instance);

        await watch.CheckAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(watch.Good);
    }

    /// <summary>An escape from here would stop the whole pod, so a store fault could never read as not ready.</summary>
    [Fact]
    public async Task REQ_BT_028_A_read_that_throws_counts_for_nothing_and_the_next_read_still_counts()
    {
        store.Reach = _ => throw new InvalidOperationException("store fault");
        var watch = Watch();
        var answer = watch.GoodAsync(default);

        await watch.CheckAsync(default);

        Assert.False(await answer);
        store.Reach = _ => Task.FromResult(true);
        await watch.CheckAsync(default);
        Assert.True(watch.Good);
    }

    [Fact]
    public void REQ_BT_028_Reads_every_2_s_limited_to_10_s_and_good_for_15_s()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), rules.Interval);
        Assert.Equal(TimeSpan.FromSeconds(10), rules.ReadLimit);
        Assert.Equal(TimeSpan.FromSeconds(15), rules.FreshFor);
    }

    [Fact]
    public async Task REQ_BT_028_A_route_waits_for_the_first_read_and_takes_its_answer()
    {
        var watch = new StoreWatch(store, clock, new StoreWatchRules { FirstWait = TimeSpan.FromMinutes(5) }, NullLogger<StoreWatch>.Instance);
        var answer = watch.GoodAsync(default);

        await watch.CheckAsync(default);

        Assert.True(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task REQ_BT_028_With_no_first_read_a_route_answers_not_good_inside_the_probe_timeout()
    {
        var watch = new StoreWatch(store, clock, new StoreWatchRules { FirstWait = TimeSpan.FromMilliseconds(20) }, NullLogger<StoreWatch>.Instance);

        Assert.False(await watch.GoodAsync(default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(new StoreWatchRules().FirstWait < TimeSpan.FromSeconds(3));
    }
}
