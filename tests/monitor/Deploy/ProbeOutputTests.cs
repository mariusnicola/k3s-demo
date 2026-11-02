using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>Runs the real lib.ps1 functions with a stand-in for wsl, so where a row runs is read from the functions themselves.</summary>
public class ProbeOutputTests
{
    private const string Ingress = "<INGRESS_IP>";
    private const string Nsenter = "sudo -n --preserve-env=PROBE_SESSION nsenter -t 4242 -n ";
    private const string Running = "'[{\"State\":{\"Status\":\"running\",\"Pid\":4242}}]'";
    private const string Stopped = "'[{\"State\":{\"Status\":\"exited\",\"Pid\":0}}]'";

    private static string SmokeDir([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "smoke"));

    private static string Function(string text, string name)
    {
        var found = Regex.Match(text, $@"function {name}(\([^\n]*\))? \{{.*?\r?\n\}}", RegexOptions.Singleline).Value;
        Assert.NotEmpty(found);
        return found;
    }

    /// <summary>The stand-in logs every command, so a row that ran where it must not is seen even when it throws.</summary>
    private static async Task<string> Run(string call, string monitor = Running, string answer = "$global:LASTEXITCODE = 0")
    {
        var lib = File.ReadAllText(Path.Combine(SmokeDir(), "lib.ps1"));
        var functions = string.Join("\n", new[] { "Get-IngressPrefix", "Invoke-Wsl", "Invoke-Probe", "Get-NatHeldPorts" }.Select(n => Function(lib, n)));
        var script = $$"""
            function wsl {
                # A native wsl.exe drops the null an empty prefix splats, and PowerShell keeps -- from reaching a function.
                $line = (@($args | Where-Object { $null -ne $_ }) -join ' ') -replace '^-d x( --cd y)? (-- )?', ''
                if ($line -match 'docker inspect') { $global:LASTEXITCODE = 0; {{monitor}}; return }
                "ran: $line" | Out-File -Append -FilePath $env:STAND_IN_LOG -Encoding utf8
                {{answer}}
            }
            $Distro = 'x'; $ProbePublish = 'y'; $MonitorContainer = 'batchtracker-monitor'; $IngressAddress = '{{Ingress}}'
            {{functions}}
            try { {{call}} } catch { "threw: $_" }
            """;
        var log = Path.GetTempFileName();
        var start = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script]) { RedirectStandardOutput = true };
        start.Environment["STAND_IN_LOG"] = log;
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var ran = File.ReadAllText(log);
        File.Delete(log);
        return output + ran;
    }

    [Theory]
    [InlineData("Invoke-Probe unauthorized http://<INGRESS_IP> /line", "./probe unauthorized http://<INGRESS_IP> /line")]
    [InlineData("Invoke-Probe ws ws://<INGRESS_IP>/ws http://<INGRESS_IP>", "./probe ws ws://<INGRESS_IP>/ws")]
    [InlineData("Invoke-Probe tcp <INGRESS_IP> 80", "./probe tcp <INGRESS_IP> 80")]
    [InlineData("Invoke-Wsl curl -s -m 15 http://<INGRESS_IP>/metrics", "curl -s -m 15 http://<INGRESS_IP>/metrics")]
    [InlineData("Invoke-Wsl curl -s -H 'Host: x' http://<INGRESS_IP>/ready", "curl -s -H Host: x http://<INGRESS_IP>/ready")]
    public async Task REQ_BT_054_An_ingress_row_runs_in_Monitors_network_namespace(string call, string command)
    {
        Assert.Contains("ran: " + Nsenter + command, await Run(call));
    }

    [Theory]
    [InlineData("Invoke-Probe http http://127.0.0.1:5320/alive", "ran: ./probe http")]
    [InlineData("Invoke-Probe ws-report ws://<HOST_IP>:5320/ws http://<INGRESS_IP>", "ran: ./probe ws-report")]
    [InlineData("Invoke-Wsl curl -s http://127.0.0.1:5320/health", "ran: curl -s")]
    [InlineData("Invoke-Wsl k3s kubectl get service <INGRESS_IP>", "ran: k3s kubectl")]
    public async Task REQ_BT_054_Any_other_row_stays_in_the_root_namespace(string call, string start)
    {
        Assert.Contains(start, await Run(call));
    }

    [Theory]
    [InlineData(Stopped)]
    [InlineData("$global:LASTEXITCODE = 1")]
    public async Task REQ_BT_054_An_ingress_row_with_no_Monitor_fails_and_never_runs_in_the_root_namespace(string monitor)
    {
        var output = await Run("Invoke-Probe unauthorized http://<INGRESS_IP> /line", monitor);

        Assert.Contains("threw: ", output);
        Assert.Contains("batchtracker-monitor", output);
        Assert.Contains("not running", output);
        Assert.DoesNotContain("ran:", output);
    }

    [Fact]
    public async Task REQ_BT_054_A_pass_adds_nothing_to_the_log()
    {
        var output = await Run("Invoke-Probe http http://127.0.0.1:1/", answer: "'frame-text'; $global:LASTEXITCODE = 0");

        Assert.DoesNotContain("frame-text", output);
    }

    [Fact]
    public async Task REQ_BT_054_A_failed_probe_still_throws_with_its_output()
    {
        var output = await Run("Invoke-Probe http http://127.0.0.1:1/", answer: "'boom'; $global:LASTEXITCODE = 1");

        Assert.Contains("threw: probe http http://127.0.0.1:1/ failed: boom", output);
    }

    [Fact]
    public async Task REQ_BT_054_Held_ports_are_the_wire_ports_of_open_container_connections_to_the_ingress()
    {
        const string Table = "'ipv4 2 tcp 6 431990 ESTABLISHED src=198.19.23.2 dst=<INGRESS_IP> sport=33306 dport=80 src=<INGRESS_IP> dst=<HOST_IP> sport=80 dport=47980 [ASSURED] mark=0 zone=0 use=2'," +
            "'ipv4 2 tcp 6 47 TIME_WAIT src=198.19.23.2 dst=<INGRESS_IP> sport=52966 dport=80 src=<INGRESS_IP> dst=<HOST_IP> sport=80 dport=46341 [ASSURED] mark=0 zone=0 use=2'," +
            "'ipv4 2 tcp 6 90 ESTABLISHED src=<HOST_IP> dst=<INGRESS_IP> sport=45000 dport=80 src=<INGRESS_IP> dst=<HOST_IP> sport=80 dport=45000 [ASSURED] mark=0 zone=0 use=2'";

        var output = await Run($"(Get-NatHeldPorts @({Table})) -join ','");

        Assert.Equal("47980", output.Trim());
    }

    [Fact]
    public void REQ_BT_054_Every_row_that_touches_the_ingress_from_wsl_goes_through_curl_so_the_namespace_rule_sees_it()
    {
        var lines = Directory.GetFiles(SmokeDir(), "check-*.ps1")
            .SelectMany(f => File.ReadLines(f).Select(l => (File: Path.GetFileName(f), Line: l)))
            .Where(l => Regex.IsMatch(l.Line, @"\bInvoke-Wsl\b|\bwsl\s+-d\b") && Regex.IsMatch(l.Line, @"Ingress(Origin|Address)|MonitorSocket|<INGRESS_IP>"))
            .ToList();

        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.True(Regex.IsMatch(l.Line, @"\bInvoke-Wsl curl\b"), $"{l.File}: {l.Line.Trim()}"));
    }
}
