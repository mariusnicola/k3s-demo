using System.Net;
using BatchTracker.Domain;
using BatchTracker.Line;

namespace BatchTracker.UnitTests;

/// <summary>The Line learns an event is done from the socket, so a step costs one read, however long the event takes.</summary>
public sealed class LineSettledTests : LineFailureTest
{
    private const int Moves = 5;

    private readonly LineSettled settled = new() { Connected = true };
    private readonly List<string?> ifMatch = [];
    private int reads;

    private static Batch At(long version, string? pending = PendingStatus.Sent) => NewBatch(pending) with { Version = version };

    /// <summary>Every read finds the event pending; `frame` says what the socket brings right after it.</summary>
    private Func<HttpRequestMessage, Task<HttpResponseMessage>> PendingThen(Action<long> frame) => AfterCreate(request =>
    {
        if (request.Method == HttpMethod.Post)
        {
            lock (ifMatch) ifMatch.Add(request.Headers.GetValues("If-Match").Single());
            return Json(HttpStatusCode.OK, At(7));
        }
        var version = Interlocked.Increment(ref reads) * 10L;
        frame(version);
        return Json(HttpStatusCode.OK, At(version), "\"read\"");
    });

    [Fact]
    public async Task REQ_BT_045_A_settled_frame_ends_the_wait_with_one_read_a_step_and_gives_the_ETag()
    {
        var engine = Engine(PendingThen(version => settled.Saw("\"frame\"", At(version + 1, null))), new LineOptions(), settled);

        await engine.RunBatchAsync(1, CancellationToken.None);

        Assert.Equal(0, log.Counts().Errors);
        Assert.Equal(Moves, reads);
        Assert.All(ifMatch, etag => Assert.Equal("\"frame\"", etag));
    }

    [Fact]
    public async Task REQ_BT_045_A_frame_no_newer_than_the_read_ends_no_wait()
    {
        var engine = Engine(PendingThen(version => settled.Saw("\"old\"", At(version, null))), new LineOptions
        {
            EventTimeout = TimeSpan.FromMilliseconds(300),
            MaxPollDelay = TimeSpan.FromMilliseconds(100)
        }, settled);

        await engine.RunBatchAsync(1, CancellationToken.None);

        Assert.Empty(ifMatch);
        Assert.True(reads >= 2, $"{reads} reads");
    }

    [Fact]
    public async Task REQ_BT_045_A_frame_with_an_event_pending_ends_no_wait()
    {
        var engine = Engine(PendingThen(version => settled.Saw("\"busy\"", At(version + 1))), new LineOptions
        {
            EventTimeout = TimeSpan.FromMilliseconds(300),
            MaxPollDelay = TimeSpan.FromMilliseconds(100)
        }, settled);

        await engine.RunBatchAsync(1, CancellationToken.None);

        Assert.Empty(ifMatch);
    }

    [Fact]
    public async Task REQ_BT_045_With_the_socket_up_a_silent_event_is_read_only_at_the_slow_rate()
    {
        var engine = Engine(PendingThen(_ => { }), new LineOptions
        {
            EventTimeout = TimeSpan.FromMilliseconds(300),
            PollDelay = TimeSpan.FromMilliseconds(1),
            MaxPollDelay = TimeSpan.FromMilliseconds(100),
            Roll = () => 0.5
        }, settled);

        await engine.RunBatchAsync(1, CancellationToken.None);

        Assert.InRange(reads, 1, 5);
    }

    [Fact]
    public async Task REQ_BT_045_A_finished_wait_leaves_no_watch_behind()
    {
        var engine = Engine(PendingThen(version => settled.Saw("\"frame\"", At(version + 1, null))), new LineOptions(), settled);

        await engine.RunBatchAsync(1, CancellationToken.None);

        Assert.Equal(0, settled.Watching);
    }

    [Fact]
    public void REQ_BT_045_A_frame_for_a_batch_nobody_waits_on_is_ignored()
    {
        settled.Saw("\"frame\"", At(1, null));

        Assert.Equal(0, settled.Watching);
    }
}
