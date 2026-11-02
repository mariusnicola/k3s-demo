using Xunit;

public class BusThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 14, 0, 0, TimeSpan.Zero);

    private static string File(DateTimeOffset at, int count) =>
        $$"""{ "at": "{{at:O}}", "rootOptions": "rw", "ioErrors": 0, "docker": true, "busThrottled": {{count}} }""";

    [Fact]
    public void REQ_BT_026_No_throttle_line_reads_healthy()
    {
        Assert.Null(BusThrottle.Problem(File(Now, 0), Now));
    }

    [Fact]
    public void REQ_BT_026_Throttle_lines_fail_and_give_the_count()
    {
        Assert.Equal("the bus emulator throttled 7 times in 60 s, error 50002", BusThrottle.Problem(File(Now, 7), Now));
    }

    [Fact]
    public void REQ_BT_026_No_file_is_no_reading_never_green()
    {
        Assert.Contains("no reading", BusThrottle.Problem(null, Now));
    }

    [Fact]
    public void REQ_BT_026_A_report_without_the_count_is_no_reading()
    {
        Assert.Contains("no reading", BusThrottle.Problem($$"""{ "at": "{{Now:O}}", "docker": true }""", Now));
    }

    [Fact]
    public void REQ_BT_026_A_broken_file_is_no_reading()
    {
        Assert.Contains("no reading", BusThrottle.Problem("{ half a fi", Now));
    }

    [Fact]
    public void REQ_BT_026_An_old_file_says_how_old()
    {
        var old = BusThrottle.Problem(File(Now - BusThrottle.MaxAge - TimeSpan.FromSeconds(1), 0), Now);

        Assert.Contains("181 s", old);
    }

    /// <summary>A real file read, because the path the Monitor passes is the only link between the watcher's report and this check.</summary>
    [Fact]
    public void REQ_BT_026_The_check_reads_the_file_at_its_path_and_names_itself()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            System.IO.File.WriteAllText(path, File(Now, 3));

            var check = BusThrottle.Check(path, Now);

            Assert.Equal("service bus throttle", check.Name);
            Assert.False(check.Healthy);
            Assert.Equal("the bus emulator throttled 3 times in 60 s, error 50002", check.Error);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void REQ_BT_026_A_missing_file_fails_the_check_and_is_never_green()
    {
        var check = BusThrottle.Check(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"), Now);

        Assert.False(check.Healthy);
        Assert.Contains("no reading", check.Error);
    }
}
