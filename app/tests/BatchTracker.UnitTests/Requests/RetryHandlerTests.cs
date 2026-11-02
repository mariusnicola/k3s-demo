using System.Net;
using BatchTracker.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BatchTracker.UnitTests;

public class RetryHandlerTests : RetryHandlerTest
{
    [Fact]
    public async Task REQ_BT_050_Delays_grow_from_200_ms_and_stop_at_30_s()
    {
        var script = new Script((_, _, _) => Status(HttpStatusCode.ServiceUnavailable));

        await Client(script).GetAsync("x");

        Assert.Equal(new[] { 200.0, 400, 800, 1600, 3200, 6400, 12800, 25600, 30000 }, waits.Select(w => w.TotalMilliseconds));
    }

    [Fact]
    public async Task REQ_BT_050_Equal_jitter_keeps_half_of_each_delay_fixed()
    {
        random = 0;

        await Client(new Script((_, _, _) => Status(HttpStatusCode.ServiceUnavailable))).GetAsync("x");

        Assert.Equal(40.5, waits.Sum(w => w.TotalSeconds), 3);
    }

    [Fact]
    public async Task REQ_BT_050_All_waits_span_81_s_at_the_top_of_the_jitter()
    {
        await Client(new Script((_, _, _) => Status(HttpStatusCode.BadGateway))).GetAsync("x");

        Assert.Equal(81.0, waits.Sum(w => w.TotalSeconds), 3);
        Assert.Equal(RetryRules.LongestWait.TotalSeconds, waits.Sum(w => w.TotalSeconds), 3);
    }

    [Fact]
    public async Task REQ_BT_050_Tenth_attempt_is_the_last_and_its_answer_comes_back()
    {
        var script = new Script((_, _, _) => Status(HttpStatusCode.GatewayTimeout));

        var response = await Client(script).GetAsync("x");

        Assert.Equal(10, script.Calls);
        Assert.Equal(RetryRules.MaxAttempts, script.Calls);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_050_Connection_failure_after_ten_attempts_says_how_many()
    {
        var script = new Script((_, _, _) => throw new HttpRequestException("refused"));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => Client(script).GetAsync("x"));

        Assert.Equal(10, script.Calls);
        Assert.Contains("10 attempts", failure.Message);
        Assert.Contains("refused", failure.Message);
        Assert.Contains(log.Lines, l => l.Contains("10 attempts"));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task REQ_BT_050_Other_answers_are_never_retried(HttpStatusCode code)
    {
        var script = new Script((_, _, _) => Status(code));

        var response = await Client(script).GetAsync("x");

        Assert.Equal(1, script.Calls);
        Assert.Equal(code, response.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task REQ_BT_050_Transient_answers_are_retried(HttpStatusCode code)
    {
        var script = new Script((call, _, _) => Status(call < 3 ? code : HttpStatusCode.OK));

        var response = await Client(script).GetAsync("x");

        Assert.Equal(3, script.Calls);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_050_Timeout_of_an_attempt_is_retried()
    {
        var script = new Script(async (call, _, token) =>
        {
            if (call == 1) await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "x").WithAttemptTimeout(TimeSpan.FromMilliseconds(20));

        var response = await Client(script).SendAsync(request);

        Assert.Equal(2, script.Calls);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_050_Caller_cancelling_during_a_wait_stops_at_once()
    {
        using var cancel = new CancellationTokenSource();
        var script = new Script((_, _, _) => Status(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(new RetryHandler(() => 1, Task.Delay, () => Now, log) { InnerHandler = script })
        {
            BaseAddress = new Uri("http://service/")
        };
        cancel.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("x", cancel.Token));

        Assert.True(script.Calls < 10);
    }

    [Fact]
    public async Task REQ_BT_050_A_post_is_sent_once_unless_it_says_it_is_safe_to_repeat()
    {
        var script = new Script((_, _, _) => Status(HttpStatusCode.ServiceUnavailable));

        await Client(script).PostAsync("x", new StringContent("body"));

        Assert.Equal(1, script.Calls);
    }

    [Fact]
    public async Task REQ_BT_050_A_repeatable_post_sends_the_same_body_and_headers_every_attempt()
    {
        var script = new Script((call, request, _) =>
        {
            Assert.Equal("key-1", request.Headers.GetValues("Idempotency-Key").Single());
            return Status(call < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "x") { Content = new StringContent("body") }.SafeToRepeat();
        request.Headers.Add("Idempotency-Key", "key-1");

        await Client(script).SendAsync(request);

        Assert.Equal(["body", "body", "body"], script.Bodies);
    }

    [Fact]
    public async Task REQ_BT_050_Single_attempt_call_is_not_retried()
    {
        var script = new Script((_, _, _) => Status(HttpStatusCode.ServiceUnavailable));
        using var request = new HttpRequestMessage(HttpMethod.Get, "x").SingleAttempt();

        await Client(script).SendAsync(request);

        Assert.Equal(1, script.Calls);
    }

    [Fact]
    public async Task REQ_BT_050_Attempts_and_throttles_are_readable_on_the_request()
    {
        var script = new Script((call, _, _) => call == 1 ? After("1") : call == 2 ? Status(HttpStatusCode.BadGateway) : Status(HttpStatusCode.OK));
        using var request = new HttpRequestMessage(HttpMethod.Get, "x");

        var response = await Client(script).SendAsync(request);

        Assert.Equal(3, request.AttemptsMade());
        Assert.Equal(1, request.TimesThrottled());
        Assert.Same(request, response.RequestMessage);
    }

    [Fact]
    public void REQ_BT_050_The_giving_up_line_names_the_path_and_never_the_query()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://service/path?secret=abc");

        Assert.Equal("GET /path", RetryHandler.Name(request));
    }
}
