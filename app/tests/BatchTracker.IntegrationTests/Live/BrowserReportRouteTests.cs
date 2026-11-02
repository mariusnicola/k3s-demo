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

/// <summary>Who may post a report, at what rate, and the hop to Live.</summary>
public class BrowserReportRouteTests(Stack stack) : BrowserReportTest(stack)
{
    [Fact]
    public async Task REQ_BT_056_A_report_from_a_foreign_origin_is_a_403()
    {
        var client = Client();
        client.DefaultRequestHeaders.Add("Origin", "https://elsewhere.example");

        var reply = await client.PostAsJsonAsync(Path, Report());

        Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode);
        Assert.Empty(sink.Seen);
    }

    [Fact]
    public async Task REQ_BT_056_A_flood_gets_a_429_with_retry_after()
    {
        var client = Host(new RateRules()).CreateClient();

        var replies = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => client.PostAsJsonAsync(Path, Report())));

        var refused = replies.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.True(int.Parse(refused.Headers.GetValues("Retry-After").Single()) > 0);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        Assert.True(replies.Count(r => r.StatusCode == HttpStatusCode.Accepted) >= 20);
    }

    [Fact]
    public async Task REQ_BT_056_Reports_leave_the_sign_in_budget_alone()
    {
        var host = Host(new RateRules());
        var client = host.CreateClient();
        await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => client.PostAsJsonAsync(Path, Report())));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_056_A_refused_report_spends_no_failure()
    {
        var host = Host(new RateRules());
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.80");
        await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => client.PostAsJsonAsync(Path, Report())));

        Assert.Equal(0, host.Services.GetRequiredService<FailureLedger>().FailuresOf("203.0.113.80"));
    }

    /// <summary>The report reaches Live after the 202, so this reads until both rows are there or five seconds pass.</summary>
    private static async Task<JsonElement> ReportPageAsync(HttpClient admin)
    {
        var page = default(JsonElement);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            await Task.Delay(50);
            page = await admin.GetFromJsonAsync<JsonElement>($"/live/logs?reportId={Id}", JsonDefaults.Options);
        } while (page.GetProperty("matched").GetInt32() < 2 && clock.Elapsed < TimeSpan.FromSeconds(5));
        return page;
    }

    /// <summary>Each side alone proves nothing about the hop, so this posts through the real API and reads from the real Live.</summary>
    [Fact]
    public async Task REQ_BT_056_A_report_crosses_from_the_real_API_to_the_real_Live_and_is_found_by_its_id()
    {
        await using var live = new LiveHost();
        var admin = live.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", Sockets.TokenFor("ada"));
        var api = Stack.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddHttpClient<ReportFlusher>().ConfigurePrimaryHttpMessageHandler(() => live.Server.CreateHandler())));
        live.Services.GetRequiredService<BatchTracker.Live.ServiceLog>().Add(new ServiceLogEntry(
            DateTimeOffset.UtcNow, ServiceLogLevel.Error, ServiceLogLevel.ErrorReason, "The request failed.", "api", "api-1", "req-7", null, null));

        var reply = await api.CreateClient().PostAsJsonAsync(Path, Report(correlationId: "req-7"));

        Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
        var page = await ReportPageAsync(admin);

        var entries = page.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        var report = Assert.Single(entries, e => e.GetProperty("service").GetString() == "browser");
        Assert.Equal(Id, report.GetProperty("reportId").GetString());
        Assert.Equal("request-failure", report.GetProperty("kind").GetString());
        Assert.Equal("/batches", report.GetProperty("route").GetString());
        Assert.Equal(503, report.GetProperty("httpStatus").GetInt32());
        Assert.Equal("browser-failure", report.GetProperty("reason").GetString());
        Assert.All(entries, e => Assert.Equal("req-7", e.GetProperty("correlationId").GetString()));
    }

    /// <summary>Only Kestrel enforces a body size with no Content-Length, so this one needs a real listener.</summary>
    [Fact]
    public async Task REQ_BT_056_A_chunked_body_over_8_KiB_is_a_413_on_a_real_listener()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ILogSink>(sink);
        await using var app = builder.Build();
        app.MapGroup("/api").MapBrowserReports();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var body = Encoding.UTF8.GetBytes($"{{\"id\":\"{Id}\",\"message\":\"{new string('x', 9000)}\"}}");
        var content = new StreamContent(new MemoryStream(body));
        content.Headers.ContentType = new("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = content };
        request.Headers.TransferEncodingChunked = true;

        var reply = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, reply.StatusCode);
        Assert.Empty(sink.Seen);
    }
}
