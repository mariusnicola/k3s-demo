using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using Microsoft.Extensions.Logging.Abstractions;

namespace BatchTracker.UnitTests;

/// <summary>Runs the real engine and API client; only the wire is scripted.</summary>
public class LineFailureTests : LineFailureTest
{
    [Fact]
    public async Task REQ_BT_045_Failed_entry_carries_every_field()
    {
        var engine = Engine(AfterCreate(_ => Json(HttpStatusCode.OK, NewBatch(), "\"e\"")),
            new LineOptions { EventTimeout = TimeSpan.Zero, PollDelay = TimeSpan.Zero });

        await engine.RunBatchAsync(4, CancellationToken.None);

        var entry = OneError();
        Assert.True(entry.At > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(LineLevel.Error, entry.Level);
        Assert.Equal(LineReason.EventTimeout, entry.Reason);
        Assert.Equal(4, entry.BatchNumber);
        Assert.Equal(BatchId, entry.BatchId);
        Assert.Equal("farm-b", entry.Farm);
        Assert.Equal("Dose1", entry.Step);
        Assert.True(entry.WaitedMs >= 0);
        Assert.Equal(200, entry.HttpStatus);
        Assert.Equal(nameof(TimeoutException), entry.ExceptionType);
        Assert.Contains("still sent", entry.ExceptionMessage);
        Assert.Equal("trace-1", entry.TraceId);
    }

    [Fact]
    public async Task REQ_BT_045_Event_timeout_says_the_line_stopped_and_the_batch_is_not_failed()
    {
        var engine = Engine(AfterCreate(_ => Json(HttpStatusCode.OK, NewBatch(), "\"e\"")),
            new LineOptions { EventTimeout = TimeSpan.FromMilliseconds(30), PollDelay = TimeSpan.FromMilliseconds(5) });

        await engine.RunBatchAsync(1, CancellationToken.None);

        var entry = OneError();
        Assert.Equal(LineReason.EventTimeout, entry.Reason);
        Assert.StartsWith("The Line stopped waiting", entry.Message);
        Assert.Contains("The batch is not failed and its event may finish later.", entry.Message);
        Assert.True(entry.WaitedMs >= 30, $"waited {entry.WaitedMs} ms");
    }

    [Fact]
    public async Task REQ_BT_045_Event_timeout_on_a_dead_event_says_blocked_not_may_finish()
    {
        var engine = Engine(AfterCreate(_ => Json(HttpStatusCode.OK, NewBatch(PendingStatus.Dead), "\"e\"")),
            new LineOptions { EventTimeout = TimeSpan.Zero, PollDelay = TimeSpan.Zero });

        await engine.RunBatchAsync(1, CancellationToken.None);

        Assert.Contains("blocked until an admin replays it", OneError().Message);
        Assert.DoesNotContain("may finish later", OneError().Message);
    }

    [Fact]
    public async Task REQ_BT_045_Default_event_timeout_is_60_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), new LineOptions().EventTimeout);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task REQ_BT_045_Http_refusal_keeps_status_and_the_api_detail(HttpStatusCode status)
    {
        var engine = Engine(AfterCreate(r => r.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, NewBatch(null), "\"e\"")
            : Json(status, new { detail = "Batch changed, read it again." })));

        await engine.RunBatchAsync(1, CancellationToken.None);

        var entry = OneError();
        Assert.Equal(LineReason.HttpRefused, entry.Reason);
        Assert.Equal((int)status, entry.HttpStatus);
        Assert.Contains($"HTTP {(int)status}", entry.Message);
        Assert.Contains("Batch changed, read it again.", entry.Message);
        Assert.Equal("Dose1", entry.Step);
    }

    [Fact]
    public async Task REQ_BT_045_Http_refusal_from_a_proxy_page_falls_back_to_the_status_text()
    {
        var engine = Engine(request => IsLogin(request)
            ? Login()
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") }));

        await engine.RunBatchAsync(1, CancellationToken.None);

        var entry = OneError();
        Assert.Equal(LineReason.HttpRefused, entry.Reason);
        Assert.Equal(502, entry.HttpStatus);
        Assert.Equal(LineStep.Create, entry.Step);
        Assert.Null(entry.BatchId);
        Assert.Contains("Bad Gateway", entry.Message);
    }

    [Fact]
    public async Task REQ_BT_045_Refused_read_while_waiting_is_a_refusal_too()
    {
        var engine = Engine(AfterCreate(_ => Json(HttpStatusCode.NotFound, new { detail = "Batch not found." })));

        await engine.RunBatchAsync(1, CancellationToken.None);

        Assert.Equal(LineReason.HttpRefused, OneError().Reason);
        Assert.Equal(404, OneError().HttpStatus);
    }
}
