using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

public class LoopsTests
{
    private static string Root([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    private readonly ConcurrentQueue<string> log = new();

    private Loops Create(Action<Check>? failed = null) => new(log.Enqueue, TimeSpan.FromMilliseconds(1), failed ?? (_ => { }));

    [Fact]
    public async Task A_failed_cycle_logs_its_reason_shows_as_failing_and_the_loop_continues()
    {
        using var stop = new CancellationTokenSource();
        var published = new ConcurrentQueue<Check>();
        var loops = Create(published.Enqueue);
        Check? duringSecond = null;
        var cycles = 0;

        await loops.Forever("report", () =>
        {
            cycles++;
            if (cycles == 1) throw new InvalidOperationException("boom");
            duringSecond = loops.Check();
            stop.Cancel();
            return Task.CompletedTask;
        }, stop.Token);

        Assert.Equal(2, cycles);
        Assert.Contains(log, l => l.StartsWith("[ERROR]") && l.Contains("report") && l.Contains("boom"));
        Assert.False(duringSecond!.Healthy);
        Assert.Contains("boom", duringSecond.Error);
        var check = Assert.Single(published);
        Assert.Equal(Loops.CheckName, check.Name);
        Assert.False(check.Healthy);
        Assert.Contains("boom", check.Error);
        Assert.True(loops.Check().Healthy);
    }

    [Fact]
    public async Task A_cycle_that_times_out_on_its_own_counts_as_a_failure()
    {
        using var stop = new CancellationTokenSource();
        var loops = Create();
        var cycles = 0;

        await loops.Forever("report", () =>
        {
            if (++cycles == 1) throw new TaskCanceledException("timed out");
            stop.Cancel();
            return Task.CompletedTask;
        }, stop.Token);

        Assert.Equal(2, cycles);
        Assert.Contains(log, l => l.StartsWith("[ERROR]") && l.Contains("timed out"));
    }

    [Fact]
    public async Task A_loop_that_stops_says_so()
    {
        using var stop = new CancellationTokenSource();
        var loops = Create();

        await loops.Forever("ticks", () => { stop.Cancel(); return Task.CompletedTask; }, stop.Token);

        Assert.Contains(log, l => l.Contains("ticks") && l.Contains("loop ended"));
        Assert.True(loops.Check().Healthy);
    }

    [Fact]
    public async Task A_loop_that_ends_early_logs_an_error_and_stays_failing()
    {
        using var stop = new CancellationTokenSource();
        var loops = Create(_ => throw new InvalidOperationException("publish broke"));

        await loops.Forever("report", () => throw new InvalidOperationException("boom"), stop.Token);

        Assert.Contains(log, l => l.StartsWith("[ERROR]") && l.Contains("loop ended") && l.Contains("publish broke"));
        var check = loops.Check();
        Assert.False(check.Healthy);
        Assert.Contains("loop ended", check.Error);
    }

    [Fact]
    public void A_good_cycle_clears_its_failure_before_it_reports()
    {
        var loops = Create();
        loops.Failed("report", "boom");
        loops.Failed("watch pods", "other");

        loops.Succeeded("report");

        var check = loops.Check();
        Assert.False(check.Healthy);
        Assert.DoesNotContain("boom", check.Error);
        Assert.Contains("other", check.Error);
    }

    [Fact]
    public void A_failed_cycle_marks_the_last_report_fresh_and_failing()
    {
        var old = new Report(DateTimeOffset.UnixEpoch, true, [new Check("cosmos", true, null), new Check(Loops.CheckName, true, null)], [], [], [], []);
        var failing = new Check(Loops.CheckName, false, "report: boom");
        var now = DateTimeOffset.UtcNow;

        var marked = Loops.Mark(old, failing, now);

        Assert.Equal(now, marked.CheckedAt);
        Assert.False(marked.Healthy);
        Assert.Equal(2, marked.Checks.Length);
        Assert.Contains(marked.Checks, c => c == failing);
    }

    [Fact]
    public void A_failed_first_cycle_still_makes_a_report()
    {
        var failing = new Check(Loops.CheckName, false, "report: boom");

        var marked = Loops.Mark(null, failing, DateTimeOffset.UtcNow);

        Assert.False(marked.Healthy);
        Assert.Equal([failing], marked.Checks);
    }

    [Fact]
    public void Every_background_loop_runs_through_Loops()
    {
        var bare = new Regex(@"Task\.Run\(|while \(!\S*(stopping|token|ApplicationStopping)", RegexOptions.IgnoreCase);
        var found = Directory.GetFiles(Path.Combine(Root(), "deploy", "monitor"), "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "Bounded.cs" && !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (Path.GetFileName(f), i + 1, line)))
            .Where(l => bare.IsMatch(l.line))
            .Select(l => $"{l.Item1}:{l.Item2}");
        Assert.Empty(found);
    }
}
