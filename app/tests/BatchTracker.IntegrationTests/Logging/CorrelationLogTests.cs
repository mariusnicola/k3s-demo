using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BatchTracker.IntegrationTests;

/// <summary>Each service as a real process, read through its real console output.</summary>
public class CorrelationLogTests
{
    private const string Sent = "log-line-id";
    private const string NoRoute = "/no-such-route";
    private const string RequestLine = "Request GET no route";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(40);

    /// <summary>Where the build put a service, next to this test's own output.</summary>
    private static string Dll(string service)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var app = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(app, "src", $"BatchTracker.{service}", "bin", output.Parent.Name, "net9.0", $"BatchTracker.{service}.dll");
    }

    /// <summary>A port nothing holds.</summary>
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Asks until the service answers, then sends the id once on a path no service routes, which every service logs.</summary>
    private static async Task AskAsync(int port)
    {
        using var http = new HttpClient();
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if ((await http.GetAsync($"http://127.0.0.1:{port}/healthz")).IsSuccessStatusCode) break;
            }
            catch (HttpRequestException) when (clock.Elapsed < Limit)
            {
            }
            await Task.Delay(100);
        }
        await http.SendAsync(Unrouted($"http://127.0.0.1:{port}"));
    }

    private static HttpRequestMessage Unrouted(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{origin}{NoRoute}");
        request.Headers.Add(Correlation.Header, Sent);
        return request;
    }

    private static async Task<bool> WaitForLineAsync(ConcurrentQueue<string> lines)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (lines.Any(l => l.Contains(RequestLine) && l.Contains(Sent))) return true;
            await Task.Delay(50);
        }
        return false;
    }

    [Theory]
    [InlineData("Api")]
    [InlineData("Worker")]
    [InlineData("Live")]
    [InlineData("Line")]
    public async Task REQ_BT_052_A_real_log_line_of_each_service_carries_the_id(string service)
    {
        var port = FreePort();
        var start = new ProcessStartInfo("dotnet", $"\"{Dll(service)}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["API_URL"] = "http://127.0.0.1:1";

        using var process = Process.Start(start)!;
        var lines = new ConcurrentQueue<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lines.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lines.Enqueue(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await AskAsync(port);
            Assert.True(await WaitForLineAsync(lines), string.Join('\n', lines.TakeLast(10)));
        }
        finally
        {
            // This test's own child: it holds a port until it goes.
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    /// <summary>Sees what any logging provider sees: the message and the scopes the logger factory attaches.</summary>
    private sealed class ScopeCapture : ILoggerProvider, ISupportExternalScope, ILogger
    {
        private IExternalScopeProvider? scopes;

        public ConcurrentQueue<string> Lines { get; } = new();

        public void SetScopeProvider(IExternalScopeProvider provider) => scopes = provider;
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopes?.Push(state);
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            scopes?.ForEachScope((scope, _) => line += " => " + scope, 0);
            Lines.Enqueue(line);
        }
    }

    /// <summary>The Dispatcher reads its lease through in-cluster config, so it cannot run as a process here.</summary>
    [Fact]
    public async Task REQ_BT_052_A_Dispatcher_log_line_carries_the_id_through_its_real_logger()
    {
        var capture = new ScopeCapture();
        await using var host = new DispatcherHost(s => s.AddSingleton<ILoggerProvider>(capture));

        await host.CreateClient().SendAsync(Unrouted(""));

        Assert.True(await WaitForLineAsync(capture.Lines), string.Join('\n', capture.Lines.TakeLast(5)));
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/metrics")]
    [InlineData("/status")]
    public async Task REQ_BT_052_A_health_route_writes_no_request_line(string path)
    {
        var capture = new ScopeCapture();
        await using var host = new DispatcherHost(s => s.AddSingleton<ILoggerProvider>(capture));

        await host.CreateClient().GetAsync(path);

        Assert.DoesNotContain(capture.Lines, l => l.Contains("Request GET"));
    }

    /// <summary>The unrouted call shows the capture reads Live, so silence on the reports is not a capture that hears nothing.</summary>
    [Fact]
    public async Task REQ_BT_052_A_Live_report_writes_no_request_line()
    {
        var capture = new ScopeCapture();
        await using var live = new LiveHost(s => s.AddSingleton<ILoggerProvider>(capture));
        var reporter = live.CreateClient().Internal();
        var now = DateTimeOffset.UtcNow;

        HttpResponseMessage[] replies =
        [
            await reporter.PostAsJsonAsync("/hops", new Hop("t", "b", "farm-a", "api", "Created", now, true), JsonDefaults.Options),
            await reporter.PostAsJsonAsync("/changes", new Versioned(BatchMachine.Create("b", "farm-a", "p", new Stamp("u", "t", now)), "etag"), JsonDefaults.Options),
            await reporter.PostAsJsonAsync("/logs", new ServiceLogEntry(now, ServiceLogLevel.Error, ServiceLogLevel.ErrorReason, "It failed.", "api", "api-1", "id-1", null, null), JsonDefaults.Options)
        ];
        await live.CreateClient().SendAsync(Unrouted(""));

        Assert.All(replies, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.True(await WaitForLineAsync(capture.Lines), string.Join('\n', capture.Lines.TakeLast(5)));
        Assert.DoesNotContain(capture.Lines, l => l.Contains("Request POST"));
    }

    [Fact]
    public async Task REQ_BT_052_A_routed_request_names_its_route_pattern_and_never_the_url()
    {
        const string Marker = "AUDITMARKER";
        var capture = new ScopeCapture();
        await using var worker = new WorkerHost(s => s.AddSingleton<ILoggerProvider>(capture));

        await worker.CreateClient().PostAsync($"/events?token={Marker}", null);

        Assert.Contains(capture.Lines, l => l.Contains("Request POST /events"));
        Assert.DoesNotContain(capture.Lines, l => l.Contains(Marker));
    }
}
