using BatchTracker.Application;

namespace BatchTracker.UnitTests;

/// <summary>Two pods read one clock, the store's, so the difference between their own clocks cannot enter a queue time.</summary>
public class StoreClockTests
{
    private static readonly DateTimeOffset StoreNow = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeReadings store = new() { Now = StoreNow };

    [Fact]
    public async Task REQ_BT_059_Two_pods_with_clocks_an_hour_apart_read_the_same_time()
    {
        var fast = new StoreClock(store, new Clock(StoreNow.AddHours(1)));
        var slow = new StoreClock(store, new Clock(StoreNow.AddHours(-1)));

        Assert.Equal(StoreNow, await fast.NowAsync(default));
        Assert.Equal(StoreNow, await slow.NowAsync(default));
    }

    [Fact]
    public async Task REQ_BT_059_Between_reads_time_runs_on_with_no_call_to_the_store()
    {
        var time = new Clock(StoreNow);
        var clock = new StoreClock(store, time);
        await clock.NowAsync(default);

        time.Now += TimeSpan.FromSeconds(29);

        Assert.Equal(StoreNow.AddSeconds(29), await clock.NowAsync(default));
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task REQ_BT_059_The_store_clock_is_read_again_after_30_seconds()
    {
        var time = new Clock(StoreNow);
        var clock = new StoreClock(store, time);
        await clock.NowAsync(default);

        time.Now += TimeSpan.FromSeconds(30);
        await clock.NowAsync(default);

        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public async Task REQ_BT_059_The_answer_is_placed_at_the_middle_of_its_round_trip()
    {
        var time = new Clock(StoreNow);
        store.WhileReading = () => time.Now += TimeSpan.FromMilliseconds(20);
        var clock = new StoreClock(store, time);

        Assert.Equal(StoreNow.AddMilliseconds(10), await clock.NowAsync(default));
    }

    [Fact]
    public async Task REQ_BT_059_A_failed_read_keeps_the_last_good_time_and_never_fails_the_caller()
    {
        var time = new Clock(StoreNow);
        var clock = new StoreClock(store, time);
        await clock.NowAsync(default);

        store.Fails = true;
        time.Now += TimeSpan.FromSeconds(31);

        Assert.Equal(StoreNow.AddSeconds(31), await clock.NowAsync(default));
    }

    [Fact]
    public async Task REQ_BT_059_A_clock_never_read_gives_no_time()
    {
        store.Fails = true;

        Assert.Null(await new StoreClock(store, new Clock(StoreNow)).NowAsync(default));
    }

    private sealed class FakeReadings : IStoreReadings
    {
        public DateTimeOffset Now { get; init; }
        public int Reads { get; private set; }
        public bool Fails { get; set; }
        public Action? WhileReading { get; set; }

        public Task<DateTimeOffset> ClockAsync(CancellationToken token)
        {
            Reads++;
            if (Fails) throw new InvalidOperationException("store down");
            WhileReading?.Invoke();
            return Task.FromResult(Now);
        }

        public Task<long> CountInProgressAsync(CancellationToken token) => Task.FromResult(0L);
    }
}
