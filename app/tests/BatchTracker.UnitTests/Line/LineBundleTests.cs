using System.Net;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using Microsoft.Extensions.Logging.Abstractions;

namespace BatchTracker.UnitTests;

/// <summary>Steps go out together on the tick, and each walk still gets its own answer.</summary>
public class LineBundleTests
{
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZGEifQ.c2lnbmF0dXJl";
    private static readonly Batch Saved = BatchMachine.Create(Guid.NewGuid().ToString(), "farm-a", "p", new Stamp("line", "trace-1", DateTimeOffset.UtcNow)) with { Pending = null };

    private readonly List<int> posted = [];
    private readonly LineLog log = new();
    private int posts;

    private static object Good(int status = 201) => new { status, etag = "\"1\"", batch = Saved };

    private sealed class Wire(Func<HttpRequestMessage, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => route(request);
    }

    private static Task<HttpResponseMessage> Json(HttpStatusCode code, object body) => Task.FromResult(new HttpResponseMessage(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, JsonDefaults.Options), System.Text.Encoding.UTF8, "application/json")
    });

    /// <summary>Signs in, then answers each POST through `answer`, given how many items it held.</summary>
    private async Task<(LineApi Api, LineEngine Engine)> Start(Func<int, Task<HttpResponseMessage>> answer, TimeSpan? every = null)
    {
        var http = new HttpClient(new Wire(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("auth/machine")) return await Json(HttpStatusCode.OK, new { token = Token });
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var count = body.RootElement.GetProperty("items").GetArrayLength();
            posted.Add(count);
            Interlocked.Increment(ref posts);
            return await answer(count);
        })) { BaseAddress = new Uri("http://api.test/api/") };
        var options = new LineOptions { BundleEvery = every ?? TimeSpan.FromMilliseconds(100) };
        var api = new LineApi(http, options, TimeProvider.System, new LineSettled());
        var engine = new LineEngine(api, log, options, TimeProvider.System, NullLogger<LineEngine>.Instance);
        await engine.StartAsync(default);
        return (api, engine);
    }

    private static LineAttempt Attempt(long number = 1) => new(number, "farm-a", TimeProvider.System);

    [Fact]
    public async Task REQ_BT_031_Three_steps_due_on_one_tick_go_as_one_post()
    {
        var (api, engine) = await Start(count => Json(HttpStatusCode.OK, new { results = Enumerable.Range(0, count).Select(_ => Good()).ToList() }));

        await Task.WhenAll(api.CreateAsync("farm-a", "a", Attempt(), default), api.CreateAsync("farm-a", "b", Attempt(), default), api.CreateAsync("farm-a", "c", Attempt(), default));
        await engine.StopAsync(default);

        Assert.Equal([3], posted);
    }

    [Fact]
    public async Task REQ_BT_031_Nothing_due_sends_no_post()
    {
        var (_, engine) = await Start(_ => throw new InvalidOperationException("no post expected"), TimeSpan.FromMilliseconds(5));

        await Task.Delay(100);
        await engine.StopAsync(default);

        Assert.Equal(0, posts);
        Assert.Equal(0, engine.Bundles().Sent);
    }

    [Fact]
    public async Task REQ_BT_031_One_refused_item_fails_its_own_step_alone()
    {
        var (api, engine) = await Start(_ => Json(HttpStatusCode.OK, new
        {
            results = new object[] { Good(), new { status = 409, detail = "Refused." } }
        }));

        var good = api.CreateAsync("farm-a", "a", Attempt(), default);
        var bad = api.CreateAsync("farm-a", "b", Attempt(2), default);
        await Task.WhenAny(Task.WhenAll(good, bad), Task.Delay(5000));
        await engine.StopAsync(default);

        Assert.True(good.IsCompletedSuccessfully);
        var failure = await Assert.ThrowsAsync<LineFailure>(() => bad);
        Assert.Equal(409, failure.Status);
        Assert.Contains("Refused.", failure.Message);
        Assert.Equal(1, engine.Bundles().FailedItems);
        Assert.False(engine.Bundles().Failing);
    }

    [Fact]
    public async Task REQ_BT_031_A_refused_post_fails_every_step_and_reads_failing()
    {
        var (api, engine) = await Start(_ => Json(HttpStatusCode.ServiceUnavailable, new { detail = "Down." }));

        var first = api.CreateAsync("farm-a", "a", Attempt(), default);
        var second = api.CreateAsync("farm-a", "b", Attempt(2), default);

        Assert.Equal(503, (await Assert.ThrowsAsync<LineFailure>(() => first)).Status);
        Assert.Equal(503, (await Assert.ThrowsAsync<LineFailure>(() => second)).Status);
        var reading = engine.Bundles();
        await engine.StopAsync(default);
        Assert.True(reading.Failing);
        Assert.Equal("HTTP 503", reading.LastFailure);
        Assert.Equal(1, reading.FailedRequests);
        Assert.Equal(2, reading.FailedItems);
    }

    [Fact]
    public async Task REQ_BT_031_A_post_that_never_arrived_reaches_every_walk_and_reads_failing()
    {
        var (api, engine) = await Start(_ => throw new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<HttpRequestException>(() => api.CreateAsync("farm-a", "a", Attempt(), default));

        var reading = engine.Bundles();
        await engine.StopAsync(default);
        Assert.True(reading.Failing);
        Assert.Equal(nameof(HttpRequestException), reading.LastFailure);
    }

    [Fact]
    public async Task REQ_BT_031_A_good_post_after_a_failed_one_clears_failing()
    {
        var calls = 0;
        var (api, engine) = await Start(_ => Interlocked.Increment(ref calls) == 1
            ? Json(HttpStatusCode.ServiceUnavailable, new { detail = "Down." })
            : Json(HttpStatusCode.OK, new { results = new[] { Good() } }));

        await Assert.ThrowsAsync<LineFailure>(() => api.CreateAsync("farm-a", "a", Attempt(), default));
        await api.CreateAsync("farm-a", "b", Attempt(), default);

        var reading = engine.Bundles();
        await engine.StopAsync(default);
        Assert.False(reading.Failing);
        Assert.Null(reading.LastFailure);
        Assert.Equal(2, reading.Sent);
    }

    [Fact]
    public void REQ_BT_031_A_failure_ages_out_when_no_post_follows()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var figures = new LineBundleFigures(clock);
        figures.Failed(3, "HTTP 503", 10);
        Assert.True(figures.Reading().Failing);

        clock.Now += TimeSpan.FromSeconds(121);

        Assert.False(figures.Reading().Failing);
        Assert.Equal("HTTP 503", figures.Reading().LastFailure);
    }

    [Fact]
    public async Task REQ_BT_031_A_reply_of_the_wrong_length_fails_the_walks_and_not_silently()
    {
        var (api, engine) = await Start(_ => Json(HttpStatusCode.OK, new { results = Array.Empty<object>() }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.CreateAsync("farm-a", "a", Attempt(), default));

        var reading = engine.Bundles();
        await engine.StopAsync(default);
        Assert.True(reading.Failing);
    }

    [Fact]
    public void REQ_BT_031_A_cancelled_walk_sends_nothing()
    {
        var bundle = new LineBundle();
        using var cancel = new CancellationTokenSource();
        _ = bundle.AddAsync(new BundleItem(BundleKinds.Create), cancel.Token);
        cancel.Cancel();

        Assert.Empty(bundle.Take());
    }
}
