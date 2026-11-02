using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Xunit;

public partial class IngressLogTests
{
    private sealed record Row(DateTimeOffset At, int Status, string? UpstreamStatus, string Path);
    private sealed record Reply(string CorrelationId, bool Available, DateTimeOffset? Since, Row[] Rows);

    private static async Task<HttpResponseMessage> Http(string url, string? cookie, IngressLog log, ProbeLimits? limits = null)
    {
        await using var host = TestHost.Create();
        IngressRoutes.Map(host, new SessionReader(PublicKey), log, limits ?? ProbeLimits.Standard(SocketLimits.Standard(null)));
        await host.StartAsync();
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(TestHost.Url(host)), url));
        if (cookie is not null) request.Headers.Add("Cookie", $"bt_session={cookie}");
        var answer = await http.SendAsync(request);
        await answer.Content.LoadIntoBufferAsync();
        await host.StopAsync();
        return answer;
    }

    private static async Task<IngressLog> Reading(string? line = null)
    {
        var log = new IngressLog(10);
        var pipe = new Pipe();
        _ = log.Follow(_ => Task.FromResult(pipe.Reader), CancellationToken.None);
        pipe.Write((line ?? Line()) + "\n");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (log.Rows(Id).Length == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        return log;
    }

    private static string LineWith(object upstream) =>
        JsonSerializer.Serialize(new { time = "2026-11-01T10:00:00+00:00", id = Id, status = 502, upstream, path = "/api/batches" });

    /// <summary>One row with this line in the log, read through the route.</summary>
    private static async Task<Row> RowThroughRoute(string line)
    {
        using var answer = await Http($"/ingress/logs?correlationId={Id}", Token([SessionReader.OpsAdmin]), await Reading(line));
        return Assert.Single(JsonSerializer.Deserialize<Reply>(await answer.Content.ReadAsStringAsync(), Web)!.Rows);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("502")]
    [InlineData("502, 200")]
    public async Task REQ_BT_056_a_row_says_what_the_upstream_answered(string upstream)
    {
        var row = await RowThroughRoute(LineWith(upstream));

        Assert.Equal(upstream, row.UpstreamStatus);
        Assert.Equal(502, row.Status);
    }

    [Fact]
    public async Task REQ_BT_056_a_row_from_before_the_upstream_field_stays_readable_with_none()
    {
        var row = await RowThroughRoute(Line());

        Assert.Null(row.UpstreamStatus);
        Assert.Equal(502, row.Status);
    }

    [Fact]
    public async Task REQ_BT_056_an_upstream_that_is_no_string_keeps_the_row_with_none()
    {
        var row = await RowThroughRoute(LineWith(502));

        Assert.Null(row.UpstreamStatus);
        Assert.Equal("/api/batches", row.Path);
    }

    [Fact]
    public async Task REQ_BT_056_anonymous_gets_401()
    {
        using var answer = await Http($"/ingress/logs?correlationId={Id}", null, await Reading());

        Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_056_a_session_without_ops_admin_gets_401()
    {
        using var answer = await Http($"/ingress/logs?correlationId={Id}", Token(["batch.read"]), await Reading());

        Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_056_an_admin_gets_the_rows_and_how_far_back_they_go()
    {
        using var answer = await Http($"/ingress/logs?correlationId={Id}", Token([SessionReader.OpsAdmin]), await Reading());

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var reply = JsonSerializer.Deserialize<Reply>(await answer.Content.ReadAsStringAsync(), Web)!;
        Assert.Equal(Id, reply.CorrelationId);
        Assert.True(reply.Available);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T10:00:00+00:00"), reply.Since);
        Assert.Equal(502, Assert.Single(reply.Rows).Status);
    }

    [Fact]
    public async Task REQ_BT_056_an_id_with_no_row_is_200_and_empty()
    {
        using var answer = await Http("/ingress/logs?correlationId=other", Token([SessionReader.OpsAdmin]), await Reading());

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Empty(JsonSerializer.Deserialize<Reply>(await answer.Content.ReadAsStringAsync(), Web)!.Rows);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?correlationId=")]
    [InlineData("?correlationId=has%20space")]
    public async Task REQ_BT_056_a_missing_or_malformed_id_answers_400(string query)
    {
        using var answer = await Http("/ingress/logs" + query, Token([SessionReader.OpsAdmin]), await Reading());

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_056_anonymous_with_a_bad_id_still_gets_401()
    {
        using var answer = await Http("/ingress/logs?correlationId=has%20space", null, await Reading());

        Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_056_with_the_log_not_being_read_it_answers_503_and_never_an_empty_list()
    {
        using var answer = await Http($"/ingress/logs?correlationId={Id}", Token([SessionReader.OpsAdmin]), new IngressLog(10));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, answer.StatusCode);
        Assert.False(JsonSerializer.Deserialize<Reply>(await answer.Content.ReadAsStringAsync(), Web)!.Available);
    }

    [Fact]
    public async Task REQ_BT_056_the_route_is_limited()
    {
        var limits = new ProbeLimits(_ => "one", burst: 0 + 1, perSecond: 1);
        await using var host = TestHost.Create();
        IngressRoutes.Map(host, new SessionReader(PublicKey), new IngressLog(10), limits);
        await host.StartAsync();
        using var http = new HttpClient();

        var logs = new Uri(new Uri(TestHost.Url(host)), "/ingress/logs");
        (await http.GetAsync(logs)).Dispose();
        using var answer = await http.GetAsync(logs);

        Assert.Equal(HttpStatusCode.TooManyRequests, answer.StatusCode);
    }
}
