using System.Threading.RateLimiting;
using BatchTracker.Infrastructure;

namespace BatchTracker.UnitTests;

public class RateLimitTests
{
    private static TokenBucketRateLimiter Bucket(RateRule rule) => RateLimits.Bucket(rule, autoReplenish: false);

    [Fact]
    public void REQ_BT_050_Constants_are_the_contract_numbers()
    {
        var rules = new RateRules();

        Assert.Equal(new RateRule(5, 10), rules.Anonymous);
        Assert.Equal(new RateRule(50, 100), rules.User);
        Assert.Equal(new RateRule(1, 1), rules.DeepReady);
    }

    [Fact]
    public void REQ_BT_050_Bucket_allows_the_burst_exactly_and_refuses_the_next()
    {
        using var bucket = Bucket(new RateRule(5, 10));

        for (var i = 0; i < 10; i++) Assert.True(bucket.AttemptAcquire().IsAcquired, $"call {i}");
        Assert.False(bucket.AttemptAcquire().IsAcquired);
    }

    [Fact]
    public void REQ_BT_050_Refusal_says_how_long_to_wait()
    {
        using var bucket = Bucket(new RateRule(5, 1));
        bucket.AttemptAcquire();

        using var refused = bucket.AttemptAcquire();

        Assert.True(refused.TryGetMetadata(MetadataName.RetryAfter, out var wait));
        Assert.True(wait > TimeSpan.Zero);
    }

    [Fact]
    public async Task REQ_BT_050_A_second_gives_back_its_rate_and_no_more()
    {
        using var bucket = Bucket(new RateRule(5, 10));
        for (var i = 0; i < 10; i++) bucket.AttemptAcquire();

        await Task.Delay(TimeSpan.FromSeconds(1.05));
        Assert.True(bucket.TryReplenish());

        for (var i = 0; i < 5; i++) Assert.True(bucket.AttemptAcquire().IsAcquired, $"call {i}");
        Assert.False(bucket.AttemptAcquire().IsAcquired);
    }

    [Fact]
    public async Task REQ_BT_050_Concurrent_calls_never_pass_the_burst()
    {
        using var bucket = Bucket(new RateRule(5, 10));

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => bucket.AttemptAcquire().IsAcquired)));

        Assert.Equal(10, results.Count(r => r));
    }

    [Theory]
    [InlineData(0.2, 1)]
    [InlineData(1.0, 1)]
    [InlineData(1.1, 2)]
    public void REQ_BT_050_Retry_after_is_whole_seconds_above_zero(double seconds, int expected)
    {
        Assert.Equal(expected, RateLimits.WholeSeconds(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("1.1.1.1, 203.0.113.9", "203.0.113.9")]
    [InlineData("1.1.1.1,  203.0.113.9 ", "203.0.113.9")]
    public void REQ_BT_050_Only_the_entry_the_ingress_added_counts_as_the_address(string header, string expected)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers["X-Forwarded-For"] = header;

        Assert.Equal(expected, ClientAddress.Of(context));
    }

    [Fact]
    public void REQ_BT_050_No_header_falls_back_to_the_connection()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.18.3.3");

        Assert.Equal("198.18.3.3", ClientAddress.Of(context));
    }
}
