using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BatchTracker.Api;
using BatchTracker.Application;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

/// <summary>What the tests of BrowserReportTests and its neighbours share.</summary>
public abstract class BrowserReportTest(Stack stack) : StackTest(stack)
{
    protected const string Path = "/api/browser-reports";
    protected const string Id = "3f2b8c1e-7a4d-4e21-9c55-0d1e2f3a4b5c";

    /// <summary>Keeps each entry with the correlation id and fields in force when it was handed over.</summary>
    protected sealed class Sink : ILogSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<(ServiceLogEntry Entry, string? Current, string? Farm, string? Batch)> Seen { get; } = new();
        public void Add(ServiceLogEntry entry)
        {
            if (entry.Service == "browser") Seen.Enqueue((entry, Correlation.Current, Correlation.Farm, Correlation.BatchId));
        }
    }

    protected readonly Sink sink = new();

    protected WebApplicationFactory<BatchTracker.Api.ApiMarker> Host(RateRules? rules = null) =>
        Stack.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ILogSink>().AddSingleton<ILogSink>(sink);
            if (rules is not null) s.RemoveAll<RateRules>().AddSingleton(rules);
        }));

    protected static object Report(
        string? id = Id, string? kind = "request-failure", string? route = "/batches", int? status = 503,
        string? correlationId = "abc-123", string? message = "It failed") =>
        new { id, kind, route, status, correlationId, message };

    protected HttpClient Client(string? bearer = null)
    {
        var client = Host().CreateClient();
        if (bearer is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", bearer);
        return client;
    }

    protected ServiceLogEntry Stored => Assert.Single(sink.Seen).Entry;

    public static TheoryData<object> Malformed() =>
    [
        Report(id: null),
        Report(id: "not-a-uuid"),
        Report(kind: null),
        Report(kind: "other"),
        Report(route: null),
        Report(route: "batches"),
        Report(route: "https://elsewhere.example/page"),
        Report(status: 99),
        Report(status: 600),
        Report(correlationId: "bad id"),
        Report(correlationId: new string('a', 65))
    ];
}
