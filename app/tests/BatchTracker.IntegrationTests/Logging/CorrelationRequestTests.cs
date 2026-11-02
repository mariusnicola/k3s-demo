using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

public class CorrelationRequestTests(Stack stack) : StackTest(stack)
{
    private const string Sent = "abc-123";

    /// <summary>A store that fails every read, to reach the unhandled exception path.</summary>
    public class BrokenStore : System.Reflection.DispatchProxy
    {
        public const string Text = "secret connection detail";
        public static IBatchStore Wrap() => Create<IBatchStore, BrokenStore>();

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => throw new InvalidOperationException(Text);
    }

    /// <summary>A request carrying the sent id, or none.</summary>
    private static HttpRequestMessage Get(string path, string? id = Sent)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (id is not null) request.Headers.TryAddWithoutValidation(Correlation.Header, id);
        return request;
    }

    /// <summary>The single id a response carries.</summary>
    private static string IdOf(HttpResponseMessage response) => Assert.Single(response.Headers.GetValues(Correlation.Header));

    /// <summary>The id in a problem body.</summary>
    private static async Task<string?> BodyIdAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty(Correlation.Key).GetString();

    private HttpClient Bearer(string user)
    {
        var client = Stack.Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Sockets.TokenFor(user));
        return client;
    }

    [Fact]
    public async Task REQ_BT_052_A_valid_id_comes_back_on_a_success()
    {
        var reply = await Stack.Api.CreateClient().SendAsync(Get("/api/healthz"));

        Assert.Equal(Sent, IdOf(reply));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("a,b")]
    [InlineData("<x>")]
    public async Task REQ_BT_052_An_invalid_id_is_replaced_and_the_request_goes_on(string sent)
    {
        var reply = await Stack.Api.CreateClient().SendAsync(Get("/api/healthz", sent));

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.NotEqual(sent, IdOf(reply));
        Assert.True(Correlation.IsValid(IdOf(reply)));
    }

    [Fact]
    public async Task REQ_BT_052_An_id_of_65_characters_is_replaced()
    {
        var sent = new string('a', 65);

        var reply = await Stack.Api.CreateClient().SendAsync(Get("/api/healthz", sent));

        Assert.NotEqual(sent, IdOf(reply));
    }

    [Fact]
    public async Task REQ_BT_052_Two_headers_are_replaced()
    {
        var request = Get("/api/healthz");
        request.Headers.TryAddWithoutValidation(Correlation.Header, "other");

        var reply = await Stack.Api.CreateClient().SendAsync(request);

        Assert.NotEqual(Sent, IdOf(reply));
        Assert.NotEqual("other", IdOf(reply));
    }

    [Fact]
    public async Task REQ_BT_052_No_header_gets_a_generated_id()
    {
        var reply = await Stack.Api.CreateClient().SendAsync(Get("/api/healthz", null));

        Assert.True(Correlation.IsValid(IdOf(reply)));
    }

    [Fact]
    public async Task REQ_BT_052_A_401_carries_the_id_in_header_and_body()
    {
        var reply = await Stack.Api.CreateClient().SendAsync(Get("/api/me"));

        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
        Assert.Equal(Sent, IdOf(reply));
        Assert.Equal(Sent, await BodyIdAsync(reply));
    }

    [Fact]
    public async Task REQ_BT_052_A_403_carries_the_id_in_header_and_body()
    {
        var reply = await Bearer("viewer").SendAsync(Get("/api/batches/farm-b/none"));

        Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode);
        Assert.Equal(Sent, IdOf(reply));
        Assert.Equal(Sent, await BodyIdAsync(reply));
    }

    [Fact]
    public async Task REQ_BT_052_A_refused_origin_carries_the_id()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/dev-login");
        request.Headers.Add(Correlation.Header, Sent);
        request.Headers.Add("Origin", "http://evil.test");
        request.Headers.Add("Cookie", Sockets.CookieFor("ada"));

        var reply = await Stack.Api.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode);
        Assert.Equal(Sent, IdOf(reply));
        Assert.Equal(Sent, await BodyIdAsync(reply));
    }

    [Fact]
    public async Task REQ_BT_052_A_429_carries_the_id_in_header_and_body()
    {
        await using var host = Stack.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.RemoveAll<FailureRules>().AddSingleton(new FailureRules { MaxWaiting = 0 })));

        var reply = await host.CreateClient().SendAsync(Get("/api/summary"));

        Assert.Equal(HttpStatusCode.TooManyRequests, reply.StatusCode);
        Assert.Equal(Sent, IdOf(reply));
        Assert.Equal(Sent, await BodyIdAsync(reply));
    }

    [Fact]
    public async Task REQ_BT_052_A_500_carries_the_id_and_no_exception_text()
    {
        await using var host = Stack.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.RemoveAll<IBatchStore>().AddSingleton(BrokenStore.Wrap())));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Sockets.TokenFor("ada"));

        var reply = await client.SendAsync(Get("/api/batches"));

        Assert.Equal(HttpStatusCode.InternalServerError, reply.StatusCode);
        Assert.Equal(Sent, IdOf(reply));
        var body = await reply.Content.ReadAsStringAsync();
        Assert.Contains(Sent, body);
        Assert.DoesNotContain(BrokenStore.Text, body);
    }

    [Fact]
    public async Task REQ_BT_052_Worker_returns_the_id()
    {
        var reply = await Stack.Worker.CreateClient().SendAsync(Get("/healthz"));

        Assert.Equal(Sent, IdOf(reply));
    }

    [Fact]
    public async Task REQ_BT_052_Live_returns_the_id()
    {
        await using var live = new LiveHost();

        var reply = await live.CreateClient().SendAsync(Get("/healthz"));

        Assert.Equal(Sent, IdOf(reply));
    }

    [Fact]
    public async Task REQ_BT_052_Line_returns_the_id()
    {
        await using var line = new LineHost(Stack.Api);

        var reply = await line.CreateClient().SendAsync(Get("/healthz"));

        Assert.Equal(Sent, IdOf(reply));
    }

    [Fact]
    public async Task REQ_BT_052_Dispatcher_returns_the_id()
    {
        await using var dispatcher = new DispatcherHost(_ => { });

        var reply = await dispatcher.CreateClient().SendAsync(Get("/healthz"));

        Assert.Equal(Sent, IdOf(reply));
    }

    [Fact]
    public async Task REQ_BT_052_A_write_keeps_the_id_on_its_pending_event()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/batches") { Content = JsonContent.Create(new { farmId = "farm-a", product = "Vaccine" }) };
        request.Headers.Add(Correlation.Header, Sent);

        var reply = await Bearer("ada").SendAsync(request);

        var body = await reply.Content.ReadFromJsonAsync<JsonElement>();
        var stored = await Stack.Store.GetAsync("farm-a", body.GetProperty("id").GetString()!, default);
        Assert.Equal(Sent, stored!.Batch.Pending!.CorrelationId);
    }
}
