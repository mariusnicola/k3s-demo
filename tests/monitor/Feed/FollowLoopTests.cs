using System.IO.Pipelines;
using System.Text;
using Xunit;

/// <summary>A follow cycle does not return while connected, so these run the real log inside the real loop on a stream held open.</summary>
public sealed class FollowLoopTests : IDisposable
{
    private const string Name = "ingress log";
    private const string Id = "row-id";

    private readonly CancellationTokenSource stop = new();
    private readonly Loops loops = new(_ => { }, TimeSpan.FromMilliseconds(1), _ => { });
    private readonly IngressLog log = new(10);
    private readonly Pipe pipe = new();
    private int cycles;

    public void Dispose() => stop.Dispose();

    private static string Row() =>
        $$"""{"time":"{{DateTimeOffset.UtcNow:O}}","id":"{{Id}}","status":200,"upstream":"200","path":"/"}""" + "\n";

    private async Task Send(string text) => await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(done(), "the loop never got there");
    }

    private Task<Stream> NoPod() => throw new HttpRequestException("no pod");

    private Task<Stream> Gone() => throw new HttpRequestException("gone");

    private Task<Stream> Stream() => Task.FromResult(pipe.Reader.AsStream());

    /// <summary>Each cycle opens with the next way given, and the last one repeats.</summary>
    private Task Follow(params Func<Task<Stream>>[] opens) => loops.Following(Name, received =>
    {
        var open = opens[Math.Min(cycles++, opens.Length - 1)];
        return log.Follow(_ => open(), stop.Token, received);
    }, stop.Token);

    private async Task Finish(Task running)
    {
        await stop.CancelAsync();
        await running;
    }

    [Fact]
    public async Task REQ_BT_026_A_follow_that_reconnected_and_receives_reads_green_while_still_running()
    {
        var running = Follow(NoPod, Stream);

        await Send(Row());
        await Until(() => log.Rows(Id).Length == 1);
        await Until(() => loops.Check().Healthy);

        Assert.False(running.IsCompleted);
        Assert.Equal(2, cycles);
        Assert.Equal(new Check(Loops.CheckName, true, null), loops.Check());
        await Finish(running);
    }

    [Fact]
    public async Task REQ_BT_026_Any_line_counts_as_receiving_because_the_controller_writes_its_own_too()
    {
        var running = Follow(NoPod, Stream);

        await Send("I1102 controller says hello\n");
        await Until(() => loops.Check().Healthy);

        Assert.Empty(log.Rows(Id));
        await Finish(running);
    }

    [Fact]
    public async Task REQ_BT_026_A_follow_connected_with_nothing_received_stays_red()
    {
        var running = Follow(NoPod, Stream);

        await Until(() => log.Reading);
        await Task.Delay(100);

        Assert.Equal("ingress log: no pod", loops.Check().Error);
        await Finish(running);
    }

    [Fact]
    public async Task REQ_BT_026_A_follow_that_only_keeps_failing_stays_red_and_keeps_trying()
    {
        var running = Follow(NoPod);

        await Until(() => cycles > 3);

        Assert.Equal("ingress log: no pod", loops.Check().Error);
        await Finish(running);
    }

    [Fact]
    public async Task REQ_BT_026_A_follow_that_breaks_after_reading_green_goes_red_again()
    {
        var running = Follow(Stream, Gone);
        await Send(Row());
        await Until(() => log.Rows(Id).Length == 1);
        Assert.True(loops.Check().Healthy);

        await pipe.Writer.CompleteAsync(new IOException("stream broke"));
        await Until(() => !loops.Check().Healthy);

        Assert.False(log.Reading);
        await Until(() => loops.Check().Error == "ingress log: gone");
        await Finish(running);
    }

    [Fact]
    public async Task REQ_BT_026_A_stream_that_ends_quietly_then_cannot_reconnect_goes_red()
    {
        var running = Follow(Stream, Gone);
        await Send(Row());
        await Until(() => log.Rows(Id).Length == 1);

        await pipe.Writer.CompleteAsync();

        await Until(() => loops.Check().Error == "ingress log: gone");
        await Finish(running);
    }

    [Fact]
    public async Task REQ_BT_026_One_follow_reading_green_leaves_another_loops_failure_alone()
    {
        loops.Failed("report", "boom");
        var running = Follow(NoPod, Stream);

        await Send(Row());
        await Until(() => log.Rows(Id).Length == 1);
        await Until(() => !(loops.Check().Error ?? "").Contains("ingress log"));

        Assert.Equal("report: boom", loops.Check().Error);
        await Finish(running);
    }
}
