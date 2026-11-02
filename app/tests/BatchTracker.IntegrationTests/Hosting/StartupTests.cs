using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BatchTracker.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace BatchTracker.IntegrationTests;

/// <summary>Starts each real service process, because only a process shows the exit code and what reaches its log.</summary>
public class StartupTests
{
    private const string Marker = "SECRETMARKER";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private static readonly Dictionary<string, IReadOnlyList<Setting>> Services = new()
    {
        ["Api"] = ServiceSettings.Api,
        ["Worker"] = ServiceSettings.Worker,
        ["Dispatcher"] = ServiceSettings.Dispatcher,
        ["Live"] = ServiceSettings.Live,
        ["Line"] = ServiceSettings.Line
    };

    private sealed record Run(int ExitCode, string Output, bool Listened);

    private static string Dll(string service)
    {
        // The test output path ends in bin/<configuration>/net9.0, so the service sits at the same place in its project.
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = output.Parent!.Name;
        var app = output.Parent.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(app, "src", $"BatchTracker.{service}", "bin", configuration, "net9.0", $"BatchTracker.{service}.dll");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The service's process with these settings changed from the environment it inherits.</summary>
    private static ProcessStartInfo StartInfo(string service, Dictionary<string, string?> changes)
    {
        var start = new ProcessStartInfo("dotnet", $"\"{Dll(service)}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{FreePort()}";
        foreach (var (name, value) in changes)
        {
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        }
        return start;
    }

    private static async Task<Run> StartAsync(string service, Dictionary<string, string?> changes)
    {
        using var process = Process.Start(StartInfo(service, changes))!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(Limit);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            // This test's own child that wrongly kept running: it holds a port until it goes.
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            var text = await output + await error;
            return new Run(0, text, text.Contains("Now listening"));
        }
        var all = await output + await error;
        return new Run(process.ExitCode, all, all.Contains("Now listening"));
    }

    private static void AssertRefused(Run run, string setting, string? value = null)
    {
        Assert.NotEqual(0, run.ExitCode);
        Assert.False(run.Listened, "a port opened before validation ended");
        Assert.Contains(setting, run.Output);
        if (value is not null) Assert.DoesNotContain(value, run.Output);
        foreach (var secret in new[] { Env.PrivateKey, Env.InternalToken, Env.LineMachineToken, Env.Cosmos, Env.Bus })
            Assert.DoesNotContain(secret, run.Output);
    }

    public static TheoryData<string, string> Required()
    {
        var rows = new TheoryData<string, string>();
        foreach (var (service, settings) in Services)
            foreach (var setting in settings.Where(s => s.Required)) rows.Add(service, setting.Name);
        return rows;
    }

    public static TheoryData<string, string, string> Malformed()
    {
        var rows = new TheoryData<string, string, string>();
        foreach (var (service, settings) in Services)
        {
            foreach (var setting in settings.Where(s => s.Kind != SettingKind.Text))
                rows.Add(service, setting.Name, $"{Marker}-not-valid");
        }
        return rows;
    }

    [Theory]
    [MemberData(nameof(Required))]
    public async Task REQ_BT_051_Missing_setting_stops_the_start_and_names_it(string service, string setting)
    {
        AssertRefused(await StartAsync(service, new() { [setting] = null }), setting);
    }

    [Theory]
    [MemberData(nameof(Required))]
    public async Task REQ_BT_051_Empty_setting_is_the_same_as_missing(string service, string setting)
    {
        AssertRefused(await StartAsync(service, new() { [setting] = "" }), setting);
    }

    [Theory]
    [MemberData(nameof(Required))]
    public async Task REQ_BT_051_Spaces_only_is_the_same_as_missing(string service, string setting)
    {
        AssertRefused(await StartAsync(service, new() { [setting] = "   " }), setting);
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task REQ_BT_051_Malformed_setting_stops_the_start_and_never_shows_the_value(string service, string setting, string value)
    {
        AssertRefused(await StartAsync(service, new() { [setting] = value }), setting, value);
    }

    [Theory]
    [InlineData("Api", "INTERNAL_TOKEN", "WORKER_URL")]
    [InlineData("Worker", "INTERNAL_TOKEN", "COSMOS_CONNECTION")]
    [InlineData("Dispatcher", "SERVICEBUS_CONNECTION", "WORKER_URL")]
    [InlineData("Live", "JWT_PUBLIC_KEY", "COSMOS_CONNECTION")]
    [InlineData("Live", "INTERNAL_TOKEN", "COSMOS_CONNECTION")]
    [InlineData("Line", "API_URL", "LINE_MACHINE_TOKEN")]
    [InlineData("Api", "MONITOR_MACHINE_TOKEN", "LINE_MACHINE_TOKEN")]
    public async Task REQ_BT_051_Every_missing_name_is_listed_at_once(string service, string first, string second)
    {
        var run = await StartAsync(service, new() { [first] = null, [second] = null });

        AssertRefused(run, first);
        Assert.Contains(second, run.Output);
    }

    [Theory]
    [InlineData("Api")]
    [InlineData("Worker")]
    [InlineData("Dispatcher")]
    [InlineData("Live")]
    public async Task REQ_BT_051_The_mongo_store_without_its_connection_stops_the_start(string service)
    {
        AssertRefused(await StartAsync(service, new() { ["BATCH_STORE"] = "mongo", ["MONGO_CONNECTION"] = null }), "MONGO_CONNECTION");
    }

    [Fact]
    public async Task REQ_BT_051_A_malformed_mongo_connection_stops_the_start_and_never_prints()
    {
        var value = $"{Marker}-not-valid";

        AssertRefused(await StartAsync("Worker", new() { ["BATCH_STORE"] = "mongo", ["MONGO_CONNECTION"] = value }), "MONGO_CONNECTION", value);
    }

    [Fact]
    public async Task REQ_BT_051_No_store_choice_stops_the_start()
    {
        AssertRefused(await StartAsync("Worker", new() { ["BATCH_STORE"] = null }), "BATCH_STORE");
    }

    [Fact]
    public async Task REQ_BT_051_A_store_outside_the_two_stops_the_start()
    {
        AssertRefused(await StartAsync("Worker", new() { ["BATCH_STORE"] = "oracle" }), "BATCH_STORE");
    }

    [Fact]
    public async Task REQ_BT_051_Missing_and_malformed_are_listed_together()

    {
        var run = await StartAsync("Api", new() { ["INTERNAL_TOKEN"] = null, ["JWT_PRIVATE_KEY"] = $"{Marker}-not-valid" });

        AssertRefused(run, "INTERNAL_TOKEN", Marker);
        Assert.Contains("JWT_PRIVATE_KEY", run.Output);
    }

    [Fact]
    public async Task REQ_BT_051_Origin_with_a_path_is_malformed()

    {
        AssertRefused(await StartAsync("Live", new() { ["ALLOWED_ORIGINS"] = "http://batch-tracker.test/app" }), "ALLOWED_ORIGINS");
    }

    [Fact]
    public void REQ_BT_051_A_setting_list_passes_when_everything_is_present()
    {
        var values = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        foreach (var settings in Services.Values)
            Assert.True(new StartupSettingsCheck(values, settings).Validate(null, new StartupSettings()).Succeeded);
    }

}
