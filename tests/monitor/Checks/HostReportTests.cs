using Xunit;

/// <summary>The watcher on Windows writes the file; Monitor reads it, so each sees the other die.</summary>
public class HostReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 21, 0, 0, TimeSpan.Zero);

    private static string File(DateTimeOffset at, string checks) =>
        $$"""{ "checkedAt": "{{at:o}}", "checks": [{{checks}}] }""";

    private const string Healthy = """{ "name": "disk C:", "healthy": true, "error": null }""";
    private const string Failing = """{ "name": "docker", "healthy": false, "error": "Docker daemon down" }""";

    [Fact]
    public void REQ_BT_058_A_fresh_report_of_healthy_checks_has_no_problem()
    {
        Assert.Null(HostReport.Problem(File(Now, Healthy), Now));
    }

    [Fact]
    public void REQ_BT_058_A_failing_check_is_named_with_its_reason()
    {
        var problem = HostReport.Problem(File(Now, $"{Healthy},{Failing}"), Now);

        Assert.Equal("docker: Docker daemon down", problem);
    }

    [Fact]
    public void REQ_BT_058_No_file_says_the_watcher_has_not_reported()
    {
        Assert.Contains("watcher", HostReport.Problem(null, Now));
    }

    [Fact]
    public void REQ_BT_058_A_report_at_the_limit_is_still_fresh()
    {
        Assert.Null(HostReport.Problem(File(Now - HostReport.MaxAge, Healthy), Now));
    }

    [Fact]
    public void REQ_BT_058_A_report_past_the_limit_means_the_watcher_died()
    {
        var problem = HostReport.Problem(File(Now - HostReport.MaxAge - TimeSpan.FromSeconds(1), Healthy), Now);

        Assert.Contains("watcher", problem);
        Assert.Contains("181 s", problem);
    }

    [Fact]
    public void REQ_BT_058_An_unreadable_report_is_a_failure()
    {
        Assert.Contains("unreadable", HostReport.Problem("{ half a fi", Now));
    }

    [Fact]
    public void REQ_BT_058_A_report_with_no_checks_proves_nothing()
    {
        Assert.Contains("no checks", HostReport.Problem(File(Now, ""), Now));
    }

    [Fact]
    public void REQ_BT_058_The_check_reads_a_real_file_and_names_itself_host()
    {
        var path = Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(path, File(Now, Failing));

            var check = HostReport.Check(path, Now);

            Assert.Equal(new Check("host", false, "docker: Docker daemon down"), check);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void REQ_BT_058_A_missing_file_fails_the_check()
    {
        var check = HostReport.Check(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), Now);

        Assert.False(check.Healthy);
    }
}
