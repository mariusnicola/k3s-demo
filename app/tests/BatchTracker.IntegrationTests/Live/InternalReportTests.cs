using System.Net;
using System.Net.Http.Json;
using BatchTracker.Application;
using BatchTracker.Infrastructure;

namespace BatchTracker.IntegrationTests;

/// <summary>A report from one of our services is not a failed sign-in, however many arrive at once.</summary>
public class InternalReportTests(Stack stack) : FailureTest(stack)
{
    private const string Pod = "198.18.0.7";

    private static ServiceLogEntry Entry() => new(
        DateTimeOffset.UtcNow, ServiceLogLevel.Error, ServiceLogLevel.ErrorReason, "It failed.", "api", "api-1", "id-1", null, null);

    /// <summary>Holds its request open until told, the way a report in flight holds a seat.</summary>
    private sealed class HeldBody(Task release) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await release;
            await stream.WriteAsync(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Entry(), JsonDefaults.Options));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Following redirects buffers a request first, which would finish the held body before it is sent.</summary>
    private static HttpClient Unbuffered(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<BatchTracker.Live.LiveMarker> host)
    {
        var client = host.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", Pod);
        return client.Internal();
    }

    [Fact]
    public async Task B_025_The_eleventh_report_in_flight_is_not_refused()
    {
        await using var live = new LiveHost();
        var host = Delayed(live, new FailureRules());
        var release = new TaskCompletionSource();
        var held = Enumerable.Range(0, new FailureRules().MaxWaiting).Select(_ =>
        {
            var body = new HeldBody(release.Task);
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Unbuffered(host).PostAsync("/logs", body);
        }).ToList();
        await Task.Delay(300);

        var eleventh = await From(host, Pod).Internal().PostAsJsonAsync("/logs", Entry(), JsonDefaults.Options);
        release.SetResult();

        Assert.Equal(HttpStatusCode.Accepted, eleventh.StatusCode);
        Assert.All(await Task.WhenAll(held), r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
    }

    [Fact]
    public async Task B_025_Seats_held_under_a_pod_address_refuse_no_report()
    {
        await using var live = new LiveHost();
        var host = Delayed(live, new FailureRules());
        var rules = new FailureRules();
        for (var i = 0; i < rules.MaxWaiting; i++) Assert.True(LedgerOf(host).TryEnter(Pod, out _));

        var reply = await From(host, Pod).Internal().PostAsJsonAsync("/logs", Entry(), JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
    }

    [Fact]
    public async Task B_025_A_valid_report_spends_no_failure()
    {
        await using var live = new LiveHost();
        var host = Delayed(live, Short);

        await From(host, Pod).Internal().PostAsJsonAsync("/logs", Entry(), JsonDefaults.Options);

        Assert.Equal(0, LedgerOf(host).FailuresOf(Pod));
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData(null)]
    public async Task B_025_A_bad_token_still_counts(string? token)
    {
        await using var live = new LiveHost();
        var host = Delayed(live, Short);
        var client = From(host, Pod);
        if (token is not null) client.DefaultRequestHeaders.Add(HttpWorkerClient.TokenHeader, token);

        var reply = await client.PostAsJsonAsync("/logs", Entry(), JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
        Assert.Equal(1, LedgerOf(host).FailuresOf(Pod));
    }
}
