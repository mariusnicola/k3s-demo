using BatchTracker.Infrastructure;

namespace BatchTracker.UnitTests;

public class FailureLedgerTests
{
    private readonly Clock clock = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));

    private FailureLedger Ledger(FailureRules? rules = null) => new(rules ?? new FailureRules(), clock);

    private static double Seconds(TimeSpan wait) => wait.TotalSeconds;

    [Fact]
    public void REQ_BT_049_Constants_are_the_contract_numbers()
    {
        var rules = new FailureRules();

        Assert.Equal(2, rules.FreeFailures);
        Assert.Equal(TimeSpan.FromSeconds(1), rules.FirstDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), rules.MaxDelay);
        Assert.Equal(TimeSpan.FromMinutes(15), rules.Quiet);
        Assert.Equal(10, rules.MaxWaiting);
        Assert.Equal(10, rules.RefuseFrom);
    }

    [Fact]
    public void REQ_BT_049_Two_free_failures_then_one_second_doubling_to_the_cap()
    {
        var ledger = Ledger();

        var waits = Enumerable.Range(0, 10).Select(_ => Seconds(ledger.Fail("a"))).ToArray();

        Assert.Equal(new double[] { 0, 0, 1, 2, 4, 8, 16, 30, 30, 30 }, waits);
    }

    [Fact]
    public void REQ_BT_049_Far_past_the_cap_the_delay_never_overflows()
    {
        var ledger = Ledger();

        for (var i = 0; i < 100_000; i++) ledger.Fail("a");

        Assert.Equal(30, Seconds(ledger.Fail("a")));
    }

    [Fact]
    public void REQ_BT_049_One_tick_before_the_quiet_period_the_count_stays()
    {
        var ledger = Ledger();
        ledger.Fail("a");
        ledger.Fail("a");

        clock.Now += TimeSpan.FromMinutes(15) - TimeSpan.FromTicks(1);

        Assert.Equal(1, Seconds(ledger.Fail("a")));
    }

    [Fact]
    public void REQ_BT_049_Exactly_at_the_quiet_period_the_count_resets()
    {
        var ledger = Ledger();
        ledger.Fail("a");
        ledger.Fail("a");
        ledger.Fail("a");

        clock.Now += TimeSpan.FromMinutes(15);

        Assert.Equal(0, Seconds(ledger.Fail("a")));
        Assert.Equal(1, ledger.FailuresOf("a"));
    }

    [Fact]
    public void REQ_BT_049_Another_failure_restarts_the_quiet_period()
    {
        var ledger = Ledger();
        ledger.Fail("a");
        clock.Now += TimeSpan.FromMinutes(14);
        ledger.Fail("a");
        clock.Now += TimeSpan.FromMinutes(14);

        Assert.Equal(1, Seconds(ledger.Fail("a")));
    }

    [Fact]
    public async Task REQ_BT_049_Many_threads_lose_no_count()
    {
        var ledger = Ledger();

        await Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => Task.Run(() => ledger.Fail("a"))));

        Assert.Equal(1000, ledger.FailuresOf("a"));
    }

    [Fact]
    public void REQ_BT_049_Eleventh_at_once_is_refused_with_the_wait()
    {
        var ledger = Ledger();
        for (var i = 0; i < 5; i++) ledger.Fail("a");
        for (var i = 0; i < 10; i++) Assert.True(ledger.TryEnter("a", out _), $"entry {i}");

        Assert.False(ledger.TryEnter("a", out var wait));

        Assert.Equal(TimeSpan.FromSeconds(4), wait);
    }

    [Fact]
    public void REQ_BT_049_Refusal_with_no_failures_still_says_a_wait_above_zero()
    {
        var ledger = Ledger();
        for (var i = 0; i < 10; i++) ledger.TryEnter("a", out _);

        Assert.False(ledger.TryEnter("a", out var wait));

        Assert.Equal(TimeSpan.FromSeconds(1), wait);
    }

    [Fact]
    public void REQ_BT_049_Leaving_frees_a_place_and_keeps_the_count()
    {
        var ledger = Ledger();
        ledger.Fail("a");
        for (var i = 0; i < 10; i++) ledger.TryEnter("a", out _);

        ledger.Leave("a");

        Assert.True(ledger.TryEnter("a", out _));
        Assert.Equal(1, ledger.FailuresOf("a"));
    }

    [Fact]
    public async Task REQ_BT_049_Concurrent_entries_never_pass_the_cap()
    {
        var ledger = Ledger();

        var entered = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => ledger.TryEnter("a", out var _))));

        Assert.Equal(10, entered.Count(e => e));
    }

    [Fact]
    public void REQ_BT_049_A_client_with_no_failures_leaves_no_row_behind()
    {
        var ledger = Ledger();

        ledger.TryEnter("a", out _);
        ledger.Leave("a");

        Assert.Equal(0, ledger.Clients);
    }

    [Fact]
    public void REQ_BT_049_Table_stays_at_its_bound()
    {
        var ledger = Ledger(new FailureRules { MaxClients = 100 });

        for (var i = 0; i < 5000; i++) ledger.Fail($"client-{i}");

        Assert.True(ledger.Clients <= 100, $"{ledger.Clients}");
    }

    [Fact]
    public void REQ_BT_049_Stale_clients_leave_first()
    {
        var ledger = Ledger(new FailureRules { MaxClients = 2 });
        for (var i = 0; i < 9; i++) ledger.Fail("stale");
        clock.Now += TimeSpan.FromMinutes(16);
        ledger.Fail("fresh");

        ledger.Fail("new");

        Assert.Equal(0, ledger.FailuresOf("stale"));
        Assert.Equal(1, ledger.FailuresOf("fresh"));
    }

    [Fact]
    public void REQ_BT_049_A_live_attacker_is_not_forgotten_when_the_table_fills()
    {
        var ledger = Ledger(new FailureRules { MaxClients = 3 });
        for (var i = 0; i < 9; i++) ledger.Fail("attacker");

        for (var i = 0; i < 1000; i++)
        {
            clock.Now += TimeSpan.FromMilliseconds(1);
            ledger.Fail($"passing-{i}");
        }

        Assert.Equal(9, ledger.FailuresOf("attacker"));
    }

    [Fact]
    public void REQ_BT_049_A_client_holding_a_place_is_not_evicted()
    {
        var ledger = Ledger(new FailureRules { MaxClients = 1 });
        ledger.Fail("waiting");
        ledger.TryEnter("waiting", out _);

        ledger.Fail("other");
        ledger.Leave("waiting");

        Assert.Equal(1, ledger.FailuresOf("waiting"));
    }

    [Fact]
    public void REQ_BT_049_After_nine_failures_the_next_is_refused_until_the_count_can_clear()
    {
        var ledger = Ledger();
        for (var i = 0; i < 9; i++) ledger.Fail("a");
        clock.Now += TimeSpan.FromMinutes(5);

        Assert.True(ledger.IsRefused("a", out var wait));
        Assert.Equal(TimeSpan.FromMinutes(10), wait);
    }

    [Fact]
    public void REQ_BT_049_After_eight_failures_the_next_still_runs()
    {
        var ledger = Ledger();
        for (var i = 0; i < 8; i++) ledger.Fail("a");

        Assert.False(ledger.IsRefused("a", out _));
    }

    [Fact]
    public void REQ_BT_049_A_quiet_period_ends_the_refusal()
    {
        var ledger = Ledger();
        for (var i = 0; i < 9; i++) ledger.Fail("a");
        clock.Now += new FailureRules().Quiet;

        Assert.False(ledger.IsRefused("a", out _));
    }

    [Fact]
    public void REQ_BT_049_A_client_with_no_count_is_not_refused_and_leaves_no_entry()
    {
        var ledger = Ledger();

        Assert.False(ledger.IsRefused("a", out _));
        Assert.Equal(0, ledger.Clients);
    }
}
