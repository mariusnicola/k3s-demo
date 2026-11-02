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

public class ProblemBodyTests(Stack stack) : StackTest(stack)
{
    /// <summary>Client signed in as a demo user through a bearer.</summary>
    private HttpClient Bearer(string user)
    {
        var client = Stack.Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Sockets.TokenFor(user));
        return client;
    }

    /// <summary>Checks the four plain answers and the id, and returns the impact for the test to judge.</summary>
    public static async Task<string> AssertPlainAsync(HttpResponseMessage reply, HttpStatusCode status)
    {
        Assert.Equal(status, reply.StatusCode);
        Assert.True(reply.Headers.Contains(Correlation.Header));
        var body = await reply.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var field in new[] { "detail", "where", "impact", "next", Correlation.Key })
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty(field).GetString()), field);
        return body.GetProperty("impact").GetString()!;
    }

    [Fact]
    public async Task REQ_BT_053_A_400_on_create()
    {
        var reply = await Bearer("ada").PostAsJsonAsync("/api/batches", new { farmId = "", product = "" });

        Assert.Equal(ProblemWords.NotChanged, await AssertPlainAsync(reply, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task REQ_BT_053_A_401_says_no_batch_was_affected()
    {
        var reply = await Stack.Api.CreateClient().GetAsync("/api/me");

        Assert.Equal(ProblemWords.NoBatch, await AssertPlainAsync(reply, HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task REQ_BT_053_A_403_on_a_farm_the_user_lacks()
    {
        var reply = await Bearer("viewer").GetAsync("/api/batches/farm-b/none");

        Assert.Equal(ProblemWords.OnlyRead, await AssertPlainAsync(reply, HttpStatusCode.Forbidden));
    }

    [Fact]
    public async Task REQ_BT_053_A_404_on_a_batch()
    {
        var reply = await Bearer("ada").GetAsync($"/api/batches/farm-a/{Guid.NewGuid()}");

        Assert.Equal(ProblemWords.OnlyRead, await AssertPlainAsync(reply, HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task REQ_BT_053_A_428_with_no_if_match()
    {
        var row = await As("olivia").CreateSettled();

        var reply = await Bearer("olivia").PostAsJsonAsync($"/api/batches/{row.Batch.FarmId}/{row.Batch.Id}/advance", new { to = "Dose1" });

        Assert.Equal(ProblemWords.NotChanged, await AssertPlainAsync(reply, HttpStatusCode.PreconditionRequired));
    }

    [Fact]
    public async Task REQ_BT_053_A_429_over_the_waiting_limit()
    {
        await using var host = Stack.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.RemoveAll<FailureRules>().AddSingleton(new FailureRules { MaxWaiting = 0 })));

        var reply = await host.CreateClient().GetAsync("/api/summary");
        await reply.Content.LoadIntoBufferAsync();
        var text = await reply.Content.ReadAsStringAsync();

        Assert.Equal(ProblemWords.NoBatch, await AssertPlainAsync(reply, HttpStatusCode.TooManyRequests));
        var body = JsonDocument.Parse(text).RootElement;
        var wait = reply.Headers.RetryAfter!.Delta!.Value.TotalSeconds;
        Assert.Contains($"Wait {wait} second", body.GetProperty("next").GetString());
        Assert.DoesNotContain("Retry-After", text);
    }

    [Fact]
    public async Task REQ_BT_053_A_500_on_a_read_has_the_words_and_nothing_of_the_exception()
    {
        await using var host = Stack.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.RemoveAll<IBatchStore>().AddSingleton(CorrelationRequestTests.BrokenStore.Wrap())));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Sockets.TokenFor("ada"));

        var reply = await client.GetAsync("/api/batches");

        Assert.Equal(ProblemWords.OnlyRead, await AssertPlainAsync(reply, HttpStatusCode.InternalServerError));
        var text = await reply.Content.ReadAsStringAsync();
        Assert.DoesNotContain(CorrelationRequestTests.BrokenStore.Text, text);
        Assert.DoesNotContain(nameof(InvalidOperationException), text);
    }

    [Fact]
    public async Task REQ_BT_053_A_Worker_401_has_the_words()
    {
        var reply = await Stack.Worker.CreateClient().PostAsJsonAsync("/nightly/run", new { });

        await AssertPlainAsync(reply, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task REQ_BT_053_A_Live_401_has_the_words()
    {
        await using var live = new LiveHost();

        var reply = await live.CreateClient().GetAsync("/ws/hops");

        await AssertPlainAsync(reply, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task REQ_BT_053_A_Line_401_has_the_words()
    {
        await using var line = new LineHost(Stack.Api);

        var reply = await line.CreateClient().GetAsync("/line");

        await AssertPlainAsync(reply, HttpStatusCode.Unauthorized);
    }
}
