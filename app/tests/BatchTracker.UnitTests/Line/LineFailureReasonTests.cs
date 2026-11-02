using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using Microsoft.Extensions.Logging.Abstractions;

namespace BatchTracker.UnitTests;

/// <summary>Failures that are not an answer from the API, and how reasons add up.</summary>
public class LineFailureReasonTests : LineFailureTest
{
    [Theory]
    [MemberData(nameof(NetworkFailures))]
    public async Task REQ_BT_045_Network_failure_after_login_keeps_the_last_status(Exception failure)
    {
        var engine = Engine(AfterCreate(_ => Task.FromException<HttpResponseMessage>(failure)));

        await engine.RunBatchAsync(1, CancellationToken.None);

        var entry = OneError();
        Assert.Equal(LineReason.NetworkFailure, entry.Reason);
        Assert.Equal(failure.GetType().Name, entry.ExceptionType);
        Assert.Equal(failure.Message, entry.ExceptionMessage);
        Assert.Equal(201, entry.HttpStatus);
        Assert.Contains("may or may not have arrived", entry.Message);
        Assert.DoesNotContain(failure.Message, entry.Message);
    }

    [Fact]
    public async Task REQ_BT_045_Network_failure_on_the_first_create_has_no_status_and_no_batch()
    {
        var engine = Engine(request => IsLogin(request) ? Login() : throw new HttpRequestException("down"));

        await engine.RunBatchAsync(1, CancellationToken.None);

        var entry = OneError();
        Assert.Equal(LineReason.NetworkFailure, entry.Reason);
        Assert.Equal(200, entry.HttpStatus);
        Assert.Null(entry.BatchId);
        Assert.Null(entry.TraceId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task REQ_BT_045_Login_refused_is_a_login_failure_not_a_refusal(HttpStatusCode status)
    {
        var engine = Engine(_ => Json(status, new { detail = "Unknown user." }));

        await engine.RunBatchAsync(1, CancellationToken.None);

        var entry = OneError();
        Assert.Equal(LineReason.LoginFailure, entry.Reason);
        Assert.Equal((int)status, entry.HttpStatus);
        Assert.Contains("could not sign in", entry.Message);
        Assert.DoesNotContain("Unknown user.", entry.Message);
        Assert.Contains("Unknown user.", entry.ExceptionMessage);
    }

    [Fact]
    public async Task REQ_BT_045_Network_down_during_login_is_a_login_failure()
    {
        var engine = Engine(_ => throw new HttpRequestException("Connection refused"));

        await engine.RunBatchAsync(1, CancellationToken.None);

        Assert.Equal(LineReason.LoginFailure, OneError().Reason);
        Assert.Null(OneError().HttpStatus);
        Assert.Equal(nameof(HttpRequestException), OneError().ExceptionType);
    }

    [Fact]
    public async Task REQ_BT_045_Html_page_with_200_is_unknown_with_the_exception_in_its_own_fields()
    {
        var engine = Engine(request => IsLogin(request)
            ? Login()
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>", Encoding.UTF8, "application/json") }));

        await engine.RunBatchAsync(1, CancellationToken.None);

        var entry = OneError();
        Assert.Equal(LineReason.Unknown, entry.Reason);
        Assert.Equal(nameof(JsonException), entry.ExceptionType);
        Assert.DoesNotContain(entry.ExceptionMessage!, entry.Message);
    }

    [Fact]
    public async Task REQ_BT_045_Missing_etag_is_unknown()
    {
        var engine = Engine(AfterCreate(_ => Json(HttpStatusCode.OK, NewBatch(null))));

        await engine.RunBatchAsync(1, CancellationToken.None);

        Assert.Equal(LineReason.Unknown, OneError().Reason);
    }

    [Fact]
    public async Task REQ_BT_045_A_token_in_a_refusal_is_masked()
    {
        var engine = Engine(AfterCreate(_ => Json(HttpStatusCode.BadRequest, new { detail = $"Header was Bearer {Token}" })));

        await engine.RunBatchAsync(1, CancellationToken.None);

        var entry = OneError();
        Assert.Equal(LineReason.HttpRefused, entry.Reason);
        Assert.DoesNotContain(Token, entry.Message);
        Assert.Contains(Secrets.Mask, entry.Message);
    }

    [Fact]
    public async Task REQ_BT_045_No_token_reaches_an_entry_even_inside_an_exception_message()
    {
        var engine = Engine(AfterCreate(_ => throw new InvalidOperationException($"Header was Authorization: Bearer {Token} and {Token}")));

        await engine.RunBatchAsync(1, CancellationToken.None);

        var text = JsonSerializer.Serialize(log.Query(new LogQuery()), JsonDefaults.Options);
        Assert.DoesNotContain(Token, text);
        Assert.Contains(Secrets.Mask, OneError().ExceptionMessage);
    }

    [Fact]
    public async Task REQ_BT_045_Shutdown_is_not_recorded_as_an_error()
    {
        using var stop = new CancellationTokenSource();
        var engine = Engine(AfterCreate(_ =>
        {
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        }));

        // Cancellation passes through, as for any async call; the point is what reached the log.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RunBatchAsync(1, stop.Token));

        Assert.Equal(0, log.Counts().Errors);
        Assert.Empty(log.Query(new LogQuery(Level: LineLevel.Error)).Entries);
    }

    [Fact]
    public async Task REQ_BT_045_Every_reason_adds_up_in_the_status()
    {
        await Engine(AfterCreate(_ => Json(HttpStatusCode.OK, NewBatch(), "\"e\"")), new LineOptions { EventTimeout = TimeSpan.Zero, PollDelay = TimeSpan.Zero })
            .RunBatchAsync(1, CancellationToken.None);
        await Engine(AfterCreate(_ => Json(HttpStatusCode.NotFound, new { detail = "x" }))).RunBatchAsync(2, CancellationToken.None);
        await Engine(AfterCreate(_ => throw new HttpRequestException("x"))).RunBatchAsync(3, CancellationToken.None);
        await Engine(_ => Json(HttpStatusCode.Unauthorized, new { detail = "x" })).RunBatchAsync(4, CancellationToken.None);
        var last = Engine(AfterCreate(_ => Json(HttpStatusCode.OK, NewBatch(null))));
        await last.RunBatchAsync(5, CancellationToken.None);

        var status = last.Status();

        Assert.Equal(5, status.Started);
        Assert.Equal(5, status.Errors);
        Assert.All(status.ErrorsByReason.Values, n => Assert.Equal(1, n));
        Assert.Equal(status.Errors, status.ErrorsByReason.Values.Sum());
    }
}
