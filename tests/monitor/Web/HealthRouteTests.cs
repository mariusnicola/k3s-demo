using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Xunit;

public class HealthRouteTests
{
    private static Report ReportAt(DateTimeOffset at, bool healthy) => new(at, healthy, [], [], [], [], []);

    private static async Task<(HttpStatusCode Status, string Body)> Read(Report? report, bool onReportPort)
    {
        var (app, ports, release) = TestHost.CreateHolding(2);
        await using var _ = app;
        var (publicPort, reportPort) = (ports[0], ports[1]);
        Routes.MapHealth(app, () => report, reportPort, ProbeLimits.Standard(SocketLimits.Standard(null)));
        await app.StartAsync();
        using var http = new HttpClient();
        using var answer = await http.GetAsync($"http://127.0.0.1:{(onReportPort ? reportPort : publicPort)}/health");
        var body = await answer.Content.ReadAsStringAsync();
        await app.StopAsync();
        release();
        return (answer.StatusCode, body);
    }

    private static async Task<(HttpStatusCode Status, bool Healthy)> Get(Report? report)
    {
        var (status, body) = await Read(report, onReportPort: true);
        return (status, JsonSerializer.Deserialize<HealthBody>(body, Web)!.Healthy);
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly NodeRow Node = new("secret-node", "worker", true, 1, 1, 1);
    private static readonly Check Failing = new("secret-check", false, "secret-error");

    private static string[] Keys(string body) =>
        JsonDocument.Parse(body).RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();

    private sealed record HealthBody(bool Healthy);

    [Fact]
    public async Task A_fresh_healthy_report_answers_200()
    {
        Assert.Equal((HttpStatusCode.OK, true), await Get(ReportAt(DateTimeOffset.UtcNow, true)));
    }

    [Fact]
    public async Task A_fresh_failing_report_answers_503()
    {
        Assert.Equal((HttpStatusCode.ServiceUnavailable, false), await Get(ReportAt(DateTimeOffset.UtcNow, false)));
    }

    [Fact]
    public async Task A_stuck_cycle_never_reads_healthy()
    {
        Assert.Equal((HttpStatusCode.ServiceUnavailable, false), await Get(ReportAt(DateTimeOffset.UtcNow.AddSeconds(-91), true)));
    }

    [Fact]
    public async Task No_report_yet_answers_503()
    {
        Assert.Equal((HttpStatusCode.ServiceUnavailable, false), await Get(null));
    }

    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    public async Task B_042_the_public_port_gives_the_same_status_and_no_report(bool healthy, HttpStatusCode expected)
    {
        var report = new Report(DateTimeOffset.UtcNow, healthy, [Failing], [Node], [], [], ["secret-issue"]);

        var (status, body) = await Read(report, onReportPort: false);

        Assert.Equal(expected, status);
        Assert.Equal(["checkedAt", "healthy"], Keys(body));
        Assert.DoesNotContain("secret", body);
    }

    [Fact]
    public async Task B_042_the_report_port_gives_the_full_report()
    {
        var report = new Report(DateTimeOffset.UtcNow, false, [Failing], [Node], [], [], ["secret-issue"]);

        var (status, body) = await Read(report, onReportPort: true);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("secret-node", body);
        Assert.Contains("secret-error", body);
    }

    [Fact]
    public async Task B_042_a_stale_report_reads_503_on_the_public_port_too()
    {
        var stale = new Report(DateTimeOffset.UtcNow.AddHours(-1), true, [], [Node], [], [], []);

        var (status, body) = await Read(stale, onReportPort: false);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.False(JsonDocument.Parse(body).RootElement.GetProperty("healthy").GetBoolean());
    }

    [Fact]
    public async Task B_042_no_report_yet_answers_503_on_the_public_port_too()
    {
        var (status, body) = await Read(null, onReportPort: false);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.False(JsonDocument.Parse(body).RootElement.GetProperty("healthy").GetBoolean());
    }

    private static string Compose([CallerFilePath] string path = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "..", "deploy", "monitor", "docker-compose.yaml"));

    [Fact]
    public void B_042_the_report_port_binds_loopback_only_and_nothing_binds_every_address()
    {
        var ports = Compose().Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("- ") && l.Contains(":5320:")).ToArray();

        Assert.Equal([$"- 127.0.0.1:5320:{Routes.ReportPort}", $"- <HOST_IP>:5320:{Routes.PublicPort}"], ports);
        Assert.DoesNotContain("0.0.0.0", Compose());
    }

    [Fact]
    public void B_042_the_memory_and_cpu_limits_stay()
    {
        Assert.Contains("mem_limit: ${CAP_MONITOR_MEMORY:?run deploy/setup.ps1}", Compose());
        Assert.Contains("cpus: ${CAP_MONITOR_CPU:?run deploy/setup.ps1}", Compose());
    }
}
