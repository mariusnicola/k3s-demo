using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;

public class GateStepTests
{
    private static string Root([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    private string? kept;

    private string RunFailingStep(IEnumerable<string> lines)
    {
        var folder = Directory.CreateTempSubdirectory("gate-step-").FullName;
        File.WriteAllLines(Path.Combine(folder, "output.txt"), lines);
        var release = Path.Combine(Root(), "deploy", "release.ps1");
        var script = $". '{release}'; try {{ Invoke-GateStep 'web tests' '{folder}' 'type output.txt & exit /b 1' }} catch {{ }}";
        var start = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script]) { RedirectStandardOutput = true };
        // The step writes its log to TEMP, and a real gate's log must not be this fixture's.
        start.Environment["TEMP"] = folder;
        using var process = Process.Start(start)!;
        var printed = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var log = Path.Combine(folder, "batchtracker-gate-failure.log");
        kept = File.Exists(log) ? File.ReadAllText(log) : null;
        Directory.Delete(folder, true);
        return printed;
    }

    [Fact]
    public void Every_failing_name_is_printed_however_long_the_output()
    {
        var names = Enumerable.Range(1, 40).Select(i => $" FAIL  src/pages/a.test.tsx > case {i}.").ToList();
        var dotnet = Enumerable.Range(1, 5).Select(i => $"  Failed Monitor.Tests.Case{i} [12 ms]").ToList();
        var stack = Enumerable.Range(1, 200).Select(i => $"    at frame {i}").ToList();

        var printed = RunFailingStep(names.Concat(dotnet).Concat(stack));

        Assert.Contains("[FAIL] gate: web tests", printed);
        foreach (var name in names.Concat(dotnet)) Assert.Contains(name.Trim(), printed);
    }

    [Fact]
    public void A_failing_step_keeps_its_whole_output_in_the_runs_temp_folder()
    {
        var stack = Enumerable.Range(1, 200).Select(i => $"    at frame {i}").ToList();

        RunFailingStep(stack);

        Assert.NotNull(kept);
        Assert.Contains("at frame 1", kept);
        Assert.Contains("at frame 200", kept);
    }
}
