using BatchTracker.Application;
using BatchTracker.Infrastructure;
using BatchTracker.Live;
using BatchTracker.Line;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BatchTracker.UnitTests;

/// <summary>What the tests of ServiceLogTests and its neighbours share.</summary>
public abstract class ServiceLogTest
{
    protected sealed class Sink : ILogSink
    {
        public List<ServiceLogEntry> Entries { get; } = [];
        public void Add(ServiceLogEntry entry) => Entries.Add(entry);
    }

    internal static ServiceLogEntry Entry(string level = ServiceLogLevel.Error, string service = "api", string? id = "abc-1", DateTimeOffset? at = null) =>
        new(at ?? DateTimeOffset.UnixEpoch, level, level == ServiceLogLevel.Error ? ServiceLogLevel.ErrorReason : ServiceLogLevel.WarningReason,
            "It failed.", service, "api-1", id, null, null);

    protected readonly Sink sink = new();

    protected ILogger Logger(string category = "BatchTracker.Test") =>
        new ServiceLogProvider(() => sink, "api", "api-1").CreateLogger(category);

    /// <summary>Stands for Live that took the call and never answers.</summary>
    protected sealed class Hang(TaskCompletionSource<TimeSpan> ended) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
            finally
            {
                ended.TrySetResult(clock.Elapsed);
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
        }
    }

    /// <summary>Stands for Live being away.</summary>
    protected sealed class Refuse(Action called) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            called();
            throw new HttpRequestException("refused");
        }
    }

    /// <summary>An entry of the browser report service, named by its report and the request it failed on.</summary>
    protected static ServiceLogEntry Report(string reportId, string? correlationId) => new(
        DateTimeOffset.UnixEpoch, "error", "browser-failure", "It failed", "browser", "api-1", correlationId, null, null,
        ReportId: reportId, Kind: "request-failure", Route: "/batches");

    /// <summary>An entry of a service on a request.</summary>
    protected static ServiceLogEntry Row(string correlationId) => new(
        DateTimeOffset.UnixEpoch, "error", "service-error", "It failed", "api", "api-1", correlationId, null, null);
}
