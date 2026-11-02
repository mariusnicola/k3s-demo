using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;

public class ReleaseEnvTests
{
    private static string Root([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    private static string Release => Path.Combine(Root(), "deploy", "release.ps1");

    private static string Example => Path.Combine(Root(), "deploy", "app", ".env.example");

    private static string[] Keys => File.ReadAllLines(Example).Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('=')[0]).ToArray();

    private static string Run(string script)
    {
        var start = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $". '{Release}'; {script}"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var printed = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return printed + error.Result;
    }

    private static string Check(string file) => Run($"try {{ Assert-AppEnv '{file}' '{Example}'; 'passed' }} catch {{ \"stopped: $_\" }}");

    private static string TempFile(string? text)
    {
        var folder = Directory.CreateTempSubdirectory("release-env-").FullName;
        var path = Path.Combine(folder, ".env");
        if (text is not null) File.WriteAllText(path, text);
        return path;
    }

    private static string Filled(params string[] except) =>
        string.Concat(Keys.Where(k => !except.Contains(k)).Select(k => $"{k}=value-of-{k}\n"));

    [Fact]
    public void Every_key_filled_passes()
    {
        Assert.Contains("passed", Check(TempFile(Filled())));
    }

    [Fact]
    public void A_missing_file_stops_and_names_the_file_and_the_keys()
    {
        var file = TempFile(null);

        var printed = Check(file);

        Assert.Contains("stopped", printed);
        Assert.Contains(".env", printed);
        foreach (var key in Keys) Assert.Contains(key, printed);
    }

    [Fact]
    public void A_missing_key_stops_and_names_it_without_any_value()
    {
        var printed = Check(TempFile(Filled("COSMOS_CONNECTION")));

        Assert.Contains("stopped", printed);
        Assert.Contains("COSMOS_CONNECTION", printed);
        Assert.DoesNotContain("value-of", printed);
    }

    [Fact]
    public void An_empty_or_blank_key_counts_as_missing_and_all_are_named()
    {
        var printed = Check(TempFile(Filled("INTERNAL_TOKEN", "LINE_MACHINE_TOKEN") + "INTERNAL_TOKEN=\nLINE_MACHINE_TOKEN=   \n"));

        Assert.Contains("INTERNAL_TOKEN", printed);
        Assert.Contains("LINE_MACHINE_TOKEN", printed);
        Assert.DoesNotContain("passed", printed);
    }

    [Fact]
    public void Each_service_gets_only_its_own_keys_and_the_private_key_stays_in_the_api()
    {
        var lines = Run("Get-SecretScript '/mnt/e/x/.env' 'batch-tracker'").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        string For(string service) => Assert.Single(lines, l => l.Contains($"generic batch-tracker-{service} "));

        Assert.Contains("JWT_PRIVATE_KEY", For("api"));
        foreach (var service in new[] { "worker", "dispatcher", "live", "nightly" })
            Assert.DoesNotContain("JWT_PRIVATE_KEY", For(service));
        Assert.Contains("LINE_MACHINE_TOKEN", For("api"));
        Assert.DoesNotContain("LINE_MACHINE_TOKEN", For("worker"));
        Assert.Contains("MONITOR_MACHINE_TOKEN", For("api"));
        foreach (var service in new[] { "worker", "dispatcher", "live", "nightly" })
            Assert.DoesNotContain("MONITOR_MACHINE_TOKEN", For(service));
        Assert.DoesNotContain("SERVICEBUS_CONNECTION", For("live"));
        Assert.Contains("(INTERNAL_TOKEN)", For("nightly"));
        Assert.All(lines, l => Assert.Contains("--from-env-file=/dev/stdin", l));
    }

    [Fact]
    public void The_monitor_gets_its_own_token_and_never_the_line_token()
    {
        var setup = File.ReadAllText(Path.Combine(Root(), "deploy", "setup.ps1"));
        var compose = File.ReadAllText(Path.Combine(Root(), "deploy", "monitor", "docker-compose.yaml"));
        var monitor = setup[setup.IndexOf("function Set-Monitor", StringComparison.Ordinal)..setup.IndexOf("function Install-App", StringComparison.Ordinal)];

        Assert.Contains("MONITOR_MACHINE_TOKEN = $Secrets.MONITOR_MACHINE_TOKEN", monitor);
        Assert.Contains("MONITOR_MACHINE_TOKEN: ${MONITOR_MACHINE_TOKEN:?run deploy/setup.ps1}", compose);
        Assert.Contains("MONGO_CONNECTION = $mongo.MONGO_MONITOR_CONNECTION", monitor);
        Assert.DoesNotContain("$Secrets.MONGO_CONNECTION", monitor);
        Assert.Contains("MONGO_CONNECTION: ${MONGO_CONNECTION:?run deploy/setup.ps1}", compose);
        Assert.DoesNotContain("LINE_MACHINE_TOKEN", monitor);
        Assert.DoesNotContain("LINE_MACHINE_TOKEN", compose);
    }

    [Fact]
    public void The_monitor_gets_the_public_key_by_config_and_never_the_private_one()
    {
        var setup = File.ReadAllText(Path.Combine(Root(), "deploy", "setup.ps1"));
        var compose = File.ReadAllText(Path.Combine(Root(), "deploy", "monitor", "docker-compose.yaml"));
        var monitor = setup[setup.IndexOf("function Set-Monitor", StringComparison.Ordinal)..setup.IndexOf("function Install-App", StringComparison.Ordinal)];

        Assert.Contains("JWT_PUBLIC_KEY", Keys);
        Assert.Contains("JWT_PUBLIC_KEY = $Secrets.JWT_PUBLIC_KEY", monitor);
        Assert.Contains("JWT_PUBLIC_KEY: ${JWT_PUBLIC_KEY:?run deploy/setup.ps1}", compose);
        Assert.DoesNotContain("JWT_PRIVATE_KEY", monitor);
        Assert.DoesNotContain("JWT_PRIVATE_KEY", compose);
    }

    [Fact]
    public void Every_key_in_the_example_is_used_by_a_service()
    {
        var script = Run("Get-SecretScript '/mnt/e/x/.env' 'batch-tracker'");

        foreach (var key in Keys) Assert.Contains(key, script);
    }

    [Fact]
    public void Generated_file_holds_a_working_p256_pair_and_two_tokens_and_prints_none_of_them()
    {
        var file = TempFile(null);

        var printed = Run($"New-AppEnv '{file}' 'Ubuntu-24.04'");

        var lines = File.ReadAllText(file);
        Assert.DoesNotContain("\r", lines);
        var values = lines.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(["INTERNAL_TOKEN", "JWT_PRIVATE_KEY", "JWT_PUBLIC_KEY", "LINE_MACHINE_TOKEN", "MONITOR_MACHINE_TOKEN"], values.Keys.Order());
        Assert.NotEqual(values["INTERNAL_TOKEN"], values["LINE_MACHINE_TOKEN"]);
        Assert.NotEqual(values["LINE_MACHINE_TOKEN"], values["MONITOR_MACHINE_TOKEN"]);
        Assert.Equal(64, values["MONITOR_MACHINE_TOKEN"].Length);
        Assert.Equal(64, values["INTERNAL_TOKEN"].Length);
        foreach (var value in values.Values) Assert.DoesNotContain(value, printed);

        using var signer = ECDsa.Create();
        signer.ImportPkcs8PrivateKey(Convert.FromBase64String(values["JWT_PRIVATE_KEY"]), out _);
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(values["JWT_PUBLIC_KEY"]), out _);
        Assert.Equal(256, signer.KeySize);
        var data = Encoding.UTF8.GetBytes("session");
        Assert.True(verifier.VerifyData(data, signer.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));
    }
}
