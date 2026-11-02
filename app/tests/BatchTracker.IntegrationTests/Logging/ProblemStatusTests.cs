using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using BatchTracker.Application;
using BatchTracker.Infrastructure;
using BatchTracker.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace BatchTracker.IntegrationTests;

public class ProblemStatusTests(Stack stack) : StackTest(stack)
{
    private const string Key = "AccountKey=abc123==;rest";
    private const string Stale = "\"00000000-0000-0000-0000-000000000000\"";

    /// <summary>A store that answers but cannot be reached.</summary>
    public class AwayStore : DispatchProxy
    {
        public static IBatchStore Wrap() => Create<IBatchStore, AwayStore>();

        protected override object? Invoke(MethodInfo? method, object?[]? args) => Task.FromResult(false);
    }

    /// <summary>Lands behind every route of a host, so any service can throw a real unhandled exception.</summary>
    private sealed class Boom : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Run(_ => throw new InvalidOperationException("outer " + Key, new TimeoutException("inner " + Key)));
        };
    }

    private static HttpClient Bearer<T>(WebApplicationFactory<T> host, string user) where T : class
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Sockets.TokenFor(user));
        return client;
    }

    private static async Task AssertBlamelessAsync<T>(WebApplicationFactory<T> host) where T : class
    {
        await using var broken = host.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter, Boom>()));

        var client = Bearer(broken, "ada");
        client.DefaultRequestHeaders.Add(HttpWorkerClient.TokenHeader, Env.InternalToken);

        var reply = await client.GetAsync("/boom");

        await ProblemBodyTests.AssertPlainAsync(reply, HttpStatusCode.InternalServerError);
        var text = await reply.Content.ReadAsStringAsync();
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("outer", text);
        Assert.DoesNotContain("inner", text);
        Assert.DoesNotContain(nameof(InvalidOperationException), text);
        Assert.DoesNotContain(" at ", text);
    }

    [Fact]
    public async Task REQ_BT_053_A_405_on_a_wrong_method() =>
        await ProblemBodyTests.AssertPlainAsync(await Bearer(Stack.Api, "ada").PutAsync("/api/me", null), HttpStatusCode.MethodNotAllowed);

    [Fact]
    public async Task REQ_BT_053_A_409_on_an_illegal_move()
    {
        var olivia = As("olivia");
        var row = await olivia.CreateSettled();

        var reply = await olivia.Advance(row.Batch, "Created", row.ETag);

        Assert.Equal(ProblemWords.NotChanged, await ProblemBodyTests.AssertPlainAsync(reply, HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task REQ_BT_053_A_412_on_a_stale_etag()
    {
        var olivia = As("olivia");
        var row = await olivia.CreateSettled();

        var reply = await olivia.Advance(row.Batch, "Dose1", Stale);

        Assert.Equal(ProblemWords.NotChanged, await ProblemBodyTests.AssertPlainAsync(reply, HttpStatusCode.PreconditionFailed));
    }

    [Fact]
    public async Task REQ_BT_053_A_415_on_text_sent_as_json()
    {
        var body = new StringContent("{}", Encoding.UTF8, "text/plain");

        var reply = await Bearer(Stack.Api, "olivia").PostAsync("/api/batches", body);

        await ProblemBodyTests.AssertPlainAsync(reply, HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task REQ_BT_053_A_422_on_a_key_reused_with_another_body()
    {
        var key = Guid.NewGuid().ToString();
        var client = Bearer(Stack.Api, "olivia");
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        await client.PostAsJsonAsync("/api/batches", new { farmId = "farm-a", product = "one-" + key });

        var reply = await client.PostAsJsonAsync("/api/batches", new { farmId = "farm-a", product = "two-" + key });

        await ProblemBodyTests.AssertPlainAsync(reply, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task REQ_BT_053_A_503_with_the_store_away()
    {
        await using var host = Stack.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.RemoveAll<IBatchStore>().AddSingleton(AwayStore.Wrap())));

        var reply = await host.CreateClient().GetAsync("/api/ready");

        await ProblemBodyTests.AssertPlainAsync(reply, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task REQ_BT_053_A_success_has_no_problem_body_and_keeps_the_id()
    {
        var reply = await Bearer(Stack.Api, "ada").GetAsync("/api/me");

        reply.EnsureSuccessStatusCode();
        Assert.NotEqual("application/problem+json", reply.Content.Headers.ContentType?.MediaType);
        Assert.True(reply.Headers.Contains(Correlation.Header));
    }

    [Fact]
    public async Task REQ_BT_053_Line_400_403_and_429()
    {
        await using var line = new LineHost(Stack.Api);
        await using var tight = line.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.RemoveAll<RateRules>().AddSingleton(new RateRules { User = new RateRule(1, 1), Anonymous = new RateRule(1, 1) })));
        var ada = Bearer(line, "ada");
        var once = tight.CreateClient();
        await once.GetAsync("/line");

        await ProblemBodyTests.AssertPlainAsync(await ada.PostAsJsonAsync("/line", new { rate = -1 }), HttpStatusCode.BadRequest);
        await ProblemBodyTests.AssertPlainAsync(await Bearer(line, "olivia").GetAsync("/line"), HttpStatusCode.Forbidden);
        var limited = await once.GetAsync("/line");
        await ProblemBodyTests.AssertPlainAsync(limited, HttpStatusCode.TooManyRequests);
        var body = await limited.Content.ReadAsStringAsync();
        Assert.Contains($"Wait {limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds} second", body);
        Assert.DoesNotContain("Retry-After", body);
    }

    [Fact]
    public async Task REQ_BT_053_Worker_422_on_a_refused_event()
    {
        var created = await As("olivia").Create();
        await As("ada").FailNext(created.Batch);
        var client = Stack.Worker.CreateClient();
        client.DefaultRequestHeaders.Add(HttpWorkerClient.TokenHeader, Env.InternalToken);
        var message = new BatchEvent(created.Batch.Pending!.EventId, created.Batch.Id, created.Batch.FarmId, created.Batch.Pending.TraceId, "Created");

        var reply = await client.PostAsJsonAsync("/events", message, JsonDefaults.Options);

        await ProblemBodyTests.AssertPlainAsync(reply, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task REQ_BT_053_Worker_503_with_the_store_away()
    {
        await using var worker = new WorkerHost(s => s.RemoveAll<IBatchStore>().AddSingleton(AwayStore.Wrap()));

        var client = worker.CreateClient();
        client.DefaultRequestHeaders.Add(HttpWorkerClient.TokenHeader, Env.InternalToken);

        await ProblemBodyTests.AssertPlainAsync(await client.GetAsync("/ready"), HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public Task REQ_BT_053_A_500_on_the_Api_tells_nothing_of_the_exception() => AssertBlamelessAsync(Stack.Api);

    [Fact]
    public Task REQ_BT_053_A_500_on_the_Worker_tells_nothing_of_the_exception() => AssertBlamelessAsync(Stack.Worker);

    [Fact]
    public async Task REQ_BT_053_A_500_on_Live_tells_nothing_of_the_exception()
    {
        await using var live = new LiveHost();
        await AssertBlamelessAsync(live);
    }

    [Fact]
    public async Task REQ_BT_053_A_500_on_the_Line_tells_nothing_of_the_exception()
    {
        await using var line = new LineHost(Stack.Api);
        await AssertBlamelessAsync(line);
    }

    [Fact]
    public async Task REQ_BT_053_A_500_on_the_Dispatcher_tells_nothing_of_the_exception()
    {
        await using var dispatcher = new DispatcherHost(_ => { });
        await AssertBlamelessAsync(dispatcher);
    }

    /// <summary>This host's console log goes through the masking formatter.</summary>
    private static void AssertMasks<T>(WebApplicationFactory<T> host) where T : class =>
        Assert.Equal(
            RedactedConsoleFormatter.FormatterName,
            host.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName);

    [Fact]
    public async Task REQ_BT_053_The_pod_log_of_every_service_goes_through_the_masking_formatter()
    {
        await using var live = new LiveHost();
        await using var line = new LineHost(Stack.Api);
        await using var dispatcher = new DispatcherHost(_ => { });

        AssertMasks(Stack.Api);
        AssertMasks(Stack.Worker);
        AssertMasks(live);
        AssertMasks(line);
        AssertMasks(dispatcher);
    }

    [Fact]
    public async Task REQ_BT_053_Dispatcher_404_and_405_have_the_words()
    {
        await using var dispatcher = new DispatcherHost(_ => { });
        var client = dispatcher.CreateClient();

        await ProblemBodyTests.AssertPlainAsync(await client.GetAsync("/nothing"), HttpStatusCode.NotFound);
        await ProblemBodyTests.AssertPlainAsync(await client.PostAsync("/status", null), HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task REQ_BT_053_Live_400_403_and_405_have_the_words()
    {
        await using var live = new LiveHost();

        await ProblemBodyTests.AssertPlainAsync(await Bearer(live, "ada").GetAsync("/live/logs?level=nope"), HttpStatusCode.BadRequest);
        await ProblemBodyTests.AssertPlainAsync(await Bearer(live, "olivia").GetAsync("/live/logs"), HttpStatusCode.Forbidden);
        await ProblemBodyTests.AssertPlainAsync(await Bearer(live, "ada").DeleteAsync("/live/logs"), HttpStatusCode.MethodNotAllowed);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/plain")]
    [InlineData("image/png")]
    public async Task T_013_A_non_json_accept_on_an_error_still_gets_the_body(string accept)
    {
        var client = Stack.Api.CreateClient();
        client.DefaultRequestHeaders.Add("Accept", accept);

        var reply = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
        Assert.Contains(Correlation.Key, await reply.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task T_013_A_non_json_accept_on_a_500_still_gets_the_body()
    {
        await using var broken = Stack.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter, Boom>()));
        var client = Bearer(broken, "ada");
        client.DefaultRequestHeaders.Add("Accept", "text/html");

        var reply = await client.GetAsync("/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, reply.StatusCode);
        Assert.Contains(Correlation.Key, await reply.Content.ReadAsStringAsync());
    }
}
