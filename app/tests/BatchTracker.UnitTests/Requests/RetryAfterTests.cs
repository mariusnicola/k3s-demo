using System.Net;
using BatchTracker.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BatchTracker.UnitTests;

/// <summary>The wait a server names against the handler's own delay.</summary>
public class RetryAfterTests : RetryHandlerTest
{
    [Fact]
    public async Task REQ_BT_050_Retry_after_in_seconds_above_the_delay_wins()
    {
        var script = new Script((call, _, _) => call == 1 ? After("3") : Status(HttpStatusCode.OK));

        await Client(script).GetAsync("x");

        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(waits));
    }

    [Fact]
    public async Task REQ_BT_050_Retry_after_as_a_date_is_honoured()
    {
        var script = new Script((call, _, _) => call == 1 ? After(Now.AddSeconds(5).ToString("R")) : Status(HttpStatusCode.OK));

        await Client(script).GetAsync("x");

        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(waits));
    }

    [Fact]
    public async Task REQ_BT_050_Retry_after_below_the_delay_leaves_the_delay()
    {
        var script = new Script((call, _, _) => call < 9 ? Status(HttpStatusCode.BadGateway) : call == 9 ? After("1") : Status(HttpStatusCode.OK));

        await Client(script).GetAsync("x");

        Assert.Equal(TimeSpan.FromSeconds(30), waits[^1]);
    }

    [Fact]
    public async Task REQ_BT_050_Retry_after_past_the_longest_delay_ends_the_retries_and_says_when()
    {
        var script = new Script((_, _, _) => After("31"));

        var response = await Client(script).GetAsync("x");

        Assert.Equal(1, script.Calls);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains(log.Lines, l => l.Contains("31 s"));
    }

    [Fact]
    public async Task REQ_BT_050_Malformed_retry_after_uses_the_delay()
    {
        random = 0;
        var script = new Script((call, _, _) => call == 1 ? After("soon") : Status(HttpStatusCode.OK));

        await Client(script).GetAsync("x");

        Assert.Equal(TimeSpan.FromMilliseconds(100), Assert.Single(waits));
    }
}
