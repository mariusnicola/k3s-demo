using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BatchTracker.Api;
using BatchTracker.Application;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

/// <summary>A browser failure becomes one service log entry, checked and masked, from anyone, at a limited rate.</summary>
public class BrowserReportTests(Stack stack) : BrowserReportTest(stack)
{
    [Fact]
    public async Task REQ_BT_056_An_anonymous_report_is_stored_as_a_service_log_entry()
    {
        var reply = await Client().PostAsJsonAsync(Path, Report());

        Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
        Assert.Contains(Id, await reply.Content.ReadAsStringAsync());
        var entry = Stored;
        Assert.Equal(("error", "browser-failure", "browser"), (entry.Level, entry.Reason, entry.Service));
        Assert.Equal((Id, "request-failure", "/batches"), (entry.ReportId, entry.Kind, entry.Route));
        Assert.Equal((503, "abc-123", "It failed"), (entry.HttpStatus, entry.CorrelationId, entry.Message));
        Assert.False(string.IsNullOrWhiteSpace(entry.Pod));
    }

    [Fact]
    public async Task REQ_BT_056_A_signed_in_report_is_stored_too()
    {
        var reply = await Client(Sockets.TokenFor("ada")).PostAsJsonAsync(Path, Report());

        Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
        Assert.Equal(Id, Stored.ReportId);
    }

    [Fact]
    public async Task REQ_BT_056_A_failure_with_no_request_has_no_status_or_id()
    {
        var reply = await Client().PostAsJsonAsync(Path, Report(kind: "render-fault", status: null, correlationId: null, message: null));

        Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
        Assert.Equal((null, null), (Stored.HttpStatus, Stored.CorrelationId));
        Assert.False(string.IsNullOrWhiteSpace(Stored.Message));
    }

    [Theory]
    [InlineData("console-error")]
    [InlineData("socket-failure")]
    [InlineData("request-failure")]
    [InlineData("render-fault")]
    public async Task REQ_BT_056_Every_kind_is_taken(string kind)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await Client().PostAsJsonAsync(Path, Report(kind: kind))).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task REQ_BT_056_A_malformed_field_is_a_400_and_stores_nothing(object report)
    {
        var reply = await Client().PostAsJsonAsync(Path, report);

        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
        Assert.Equal("application/problem+json", reply.Content.Headers.ContentType?.MediaType);
        Assert.Empty(sink.Seen);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"status\":\"high\"}")]
    public async Task REQ_BT_056_A_body_that_is_no_report_is_a_400(string body)
    {
        var reply = await Client().PostAsync(Path, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
        Assert.Empty(sink.Seen);
    }

    [Fact]
    public async Task REQ_BT_056_A_body_of_another_type_is_a_400()
    {
        var reply = await Client().PostAsync(Path, new StringContent("hello", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_056_A_body_over_8_KiB_is_a_413_and_stores_nothing()
    {
        var reply = await Client().PostAsJsonAsync(Path, Report(message: new string('x', 9000)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, reply.StatusCode);
        Assert.Equal("application/problem+json", reply.Content.Headers.ContentType?.MediaType);
        Assert.Empty(sink.Seen);
    }

    [Fact]
    public async Task REQ_BT_056_The_message_is_cut_at_2000()
    {
        await Client().PostAsJsonAsync(Path, Report(message: new string('x', 7000)));

        Assert.True(Stored.Message.Length <= 2000, $"{Stored.Message.Length}");
    }

    [Theory]
    [InlineData("/batches?token=secret", "/batches")]
    [InlineData("/batches#token", "/batches")]
    [InlineData("/batches?a=1#b", "/batches")]
    public async Task REQ_BT_056_Query_and_fragment_are_dropped(string route, string stored)
    {
        await Client().PostAsJsonAsync(Path, Report(route: route));

        Assert.Equal(stored, Stored.Route);
    }

    [Fact]
    public async Task REQ_BT_056_The_route_is_cut_at_200()
    {
        await Client().PostAsJsonAsync(Path, Report(route: "/" + new string('a', 500)));

        Assert.True(Stored.Route!.Length <= 200, $"{Stored.Route.Length}");
    }

    [Fact]
    public async Task REQ_BT_056_Secrets_are_masked_in_message_and_route()
    {
        const string secret = "AccountKey=abcdef0123456789";

        await Client().PostAsJsonAsync(Path, Report(route: "/page/" + secret, message: "Failed with " + secret));

        Assert.DoesNotContain("abcdef0123456789", Stored.Message);
        Assert.DoesNotContain("abcdef0123456789", Stored.Route);
    }

    [Fact]
    public async Task REQ_BT_056_Caller_text_stays_out_of_baggage()
    {
        await Client().PostAsJsonAsync(Path, Report(correlationId: "the-reported-id", message: "farm-a b1"));

        var seen = Assert.Single(sink.Seen);
        Assert.NotEqual("the-reported-id", seen.Current);
        Assert.Null(seen.Farm);
        Assert.Null(seen.Batch);
    }
}
