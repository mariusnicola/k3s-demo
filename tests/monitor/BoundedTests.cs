using Xunit;

public class BoundedTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task A_call_that_never_answers_gives_the_fallback_inside_the_limit()
    {
        var never = new TaskCompletionSource<int>();
        var answer = await Bounded.Within(() => never.Task, Limit, -1).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(-1, answer);
    }

    [Fact]
    public async Task A_call_that_fails_gives_the_fallback()
    {
        Assert.Equal(-1, await Bounded.Within<int>(() => throw new InvalidOperationException("down"), Limit, -1));
    }

    [Fact]
    public async Task A_call_that_answers_gives_its_answer()
    {
        Assert.Equal(7, await Bounded.Within(() => Task.FromResult(7), Limit, -1));
    }

    [Theory]
    [InlineData(90, false)]
    [InlineData(91, true)]
    public void A_report_past_90_s_is_stale(int seconds, bool stale)
    {
        var now = new DateTimeOffset(2026, 10, 31, 7, 0, 0, TimeSpan.Zero);
        Assert.Equal(stale, Bounded.IsStale(now.AddSeconds(-seconds), now));
    }
}
