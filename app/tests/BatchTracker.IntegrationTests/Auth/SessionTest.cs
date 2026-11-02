using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BatchTracker.IntegrationTests;

/// <summary>What the tests of SessionTests and its neighbours share.</summary>
public abstract class SessionTest(Stack stack) : StackTest(stack)
{
    protected const string Evil = "http://evil.example";

    protected HttpClient Bare() => Stack.Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    protected async Task<string> SessionOf(string user)
    {
        var response = await Bare().PostAsJsonAsync("/api/auth/dev-login", new { user });
        response.EnsureSuccessStatusCode();
        var header = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        return header.Split(';')[0];
    }

    protected static HttpRequestMessage Request(HttpMethod method, string path, string? cookie, string? origin, HttpContent? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body };
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    protected async Task<HttpStatusCode> MeWithCookie(string cookie) =>
        (await Bare().SendAsync(Request(HttpMethod.Get, "/api/me", cookie, null))).StatusCode;

    protected static HttpClient WithSetting(WebApplicationFactory<BatchTracker.Api.ApiMarker> host, string name, string value) =>
        host.WithWebHostBuilder(b => b.UseSetting(name, value))
            .CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    public static TheoryData<string> Origins() => new() { Evil, "null", "http://batch-tracker.test:8080", "https://batch-tracker.test" };

    protected Task<HttpResponseMessage> PostMachine(HttpClient client, string? value)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/machine");
        if (value is not null) request.Headers.Add(MachineCredential.Header, value);
        return client.SendAsync(request);
    }

    protected sealed class TwoDaysAgo : TimeProvider

    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(-2);
    }
}
