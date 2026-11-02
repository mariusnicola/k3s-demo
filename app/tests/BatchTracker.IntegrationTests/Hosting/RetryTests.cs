using System.Net;
using System.Net.Http.Json;
using BatchTracker.Domain;
using BatchTracker.Line;
using Microsoft.Extensions.DependencyInjection;

namespace BatchTracker.IntegrationTests;

public class RetryTests(Stack stack) : StackTest(stack)
{
    /// <summary>Lets the request reach the API, then loses the reply, the way a dropped connection does.</summary>
    private sealed class LoseReply(HttpMessageHandler inner, Func<HttpRequestMessage, bool> when) : DelegatingHandler(inner)
    {
        private int lost;
        public int Lost => lost;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (when(request) && Interlocked.CompareExchange(ref lost, 1, 0) == 0)
                throw new HttpRequestException("reply lost after the store wrote");
            return response;
        }
    }

    /// <summary>Answers without reaching the API, so the store never changes.</summary>
    private sealed class Answer(HttpMessageHandler inner, Func<HttpRequestMessage, int, HttpStatusCode?> answer) : DelegatingHandler(inner)
    {
        private int calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            answer(request, calls) is { } code && ++calls > 0
                ? Task.FromResult(new HttpResponseMessage(code))
                : base.SendAsync(request, token);
    }

    private readonly List<LineHost> hosts = [];

    public override async Task DisposeAsync()
    {
        foreach (var host in hosts) await host.DisposeAsync();
    }

    private (LineApi Api, Func<int> Lost) LineLosing(Func<HttpRequestMessage, bool> when)
    {
        LoseReply? lose = null;
        var host = new LineHost(Stack.Api, wrap: inner => lose = new LoseReply(inner, when));
        hosts.Add(host);
        return (host.Services.GetRequiredService<LineEngine>().Api, () => lose!.Lost);
    }

    private LineApi LineAnswering(Func<HttpRequestMessage, int, HttpStatusCode?> answer)
    {
        var host = new LineHost(Stack.Api, wrap: inner => new Answer(inner, answer));
        hosts.Add(host);
        return host.Services.GetRequiredService<LineEngine>().Api;
    }

    private async Task<List<Batch>> BatchesOfAsync(string product) =>
        (await Stack.Store.FindForReportsAsync(null, default)).Where(b => b.Product == product).ToList();

    private static LineAttempt Attempt() => new(1, "farm-a", TimeProvider.System);

    /// <summary>Every step travels in a bundle, so losing or refusing one means the POST that carries it.</summary>
    private static bool IsCreate(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/batches/bundle");

    private static bool IsAdvance(HttpRequestMessage request) => IsCreate(request);

    [Fact]
    public async Task REQ_BT_050_Create_whose_reply_was_lost_makes_one_batch()
    {
        var product = "lost-create-" + Guid.NewGuid();
        var (api, lost) = LineLosing(IsCreate);

        var batch = await api.CreateAsync("farm-a", product, Attempt(), default);

        var saved = Assert.Single(await BatchesOfAsync(product));
        Assert.Equal(1, lost());
        Assert.Equal(batch.Id, saved.Id);
        Assert.Equal(1, saved.Version);
    }

    [Fact]
    public async Task REQ_BT_050_Advance_whose_reply_was_lost_is_done_with_one_write()
    {
        var (api, lost) = LineLosing(IsAdvance);
        var row = await As("olivia").CreateSettled();

        await api.AdvanceAsync(row.Batch, BatchState.Dose1, Attempt(), default);

        var saved = (await Stack.Store.GetAsync(row.Batch.FarmId, row.Batch.Id, default))!.Batch;
        Assert.Equal(1, lost());
        Assert.Equal(BatchState.Dose1, saved.State);
        Assert.Equal(row.Batch.Version + 1, saved.Version);
    }

    [Fact]
    public async Task REQ_BT_050_A_412_after_a_retry_with_the_batch_unmoved_stays_a_failure()
    {
        var api = LineAnswering((request, calls) =>
            !IsAdvance(request) ? null : calls == 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.PreconditionFailed);
        var row = await As("olivia").CreateSettled();

        var failure = await Assert.ThrowsAsync<LineFailure>(() =>
            api.AdvanceAsync(row.Batch, BatchState.Dose1, Attempt(), default));

        Assert.Equal(412, failure.Status);
    }

    [Fact]
    public async Task REQ_BT_050_A_412_on_the_first_attempt_stays_a_failure()
    {
        var api = LineAnswering((request, _) => IsAdvance(request) ? HttpStatusCode.PreconditionFailed : null);
        var row = await As("olivia").CreateSettled();

        var failure = await Assert.ThrowsAsync<LineFailure>(() =>
            api.AdvanceAsync(row.Batch, BatchState.Dose1, Attempt(), default));

        Assert.Equal(412, failure.Status);
    }

    [Fact]
    public async Task REQ_BT_050_Line_failure_after_all_attempts_says_how_many()
    {
        var api = LineAnswering((request, _) => IsCreate(request) ? HttpStatusCode.TooManyRequests : null);
        var attempt = Attempt();

        var failure = await Assert.ThrowsAsync<LineFailure>(() => api.CreateAsync("farm-a", "throttled", attempt, default));

        Assert.Equal(429, failure.Status);
        Assert.Contains("10 attempts", failure.Message);
        Assert.Equal(10, api.Throttled);
    }

    [Fact]
    public async Task REQ_BT_050_A_throttled_line_shows_it_in_status_and_logs()
    {
        var host = new LineHost(Stack.Api, wrap: inner => new Throttle(inner));
        hosts.Add(host);
        var engine = host.Services.GetRequiredService<LineEngine>();
        var log = host.Services.GetRequiredService<LineLog>();

        engine.Start(5);
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (engine.Status().Throttled == 0 && start.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        engine.Stop();

        Assert.True(engine.Status().Throttled > 0);
        var entry = log.Query(new LogQuery(null, null, null, null, null, null, null)).Entries.First(e => e.HttpStatus == 429);
        Assert.Contains("HTTP 429", entry.Message);
    }

    /// <summary>A wait past the longest delay ends the retries at once, so the test needs no 81 s.</summary>
    private sealed class Throttle(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!IsCreate(request)) return base.SendAsync(request, token);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "31");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task REQ_BT_050_Same_key_and_body_returns_the_same_batch_and_no_second_write()
    {
        var product = "same-key-" + Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var olivia = As("olivia");

        var first = await PostAsync(olivia, key, "farm-a", product);
        var second = await PostAsync(olivia, key, "farm-a", product);

        var one = await ApiUser.ReadBatch(first);
        var two = await ApiUser.ReadBatch(second);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(one.Id, two.Id);
        Assert.Equal(1, Assert.Single(await BatchesOfAsync(product)).Version);
    }

    [Fact]
    public async Task REQ_BT_050_Same_key_with_another_body_is_refused()
    {
        var key = Guid.NewGuid().ToString();
        var olivia = As("olivia");
        await PostAsync(olivia, key, "farm-a", "body-one-" + key);

        var second = await PostAsync(olivia, key, "farm-a", "body-two-" + key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Empty(await BatchesOfAsync("body-two-" + key));
    }

    [Fact]
    public async Task REQ_BT_050_Two_users_with_one_key_get_their_own_batches()
    {
        var key = Guid.NewGuid().ToString();
        var product = "shared-key-" + key;

        await PostAsync(As("olivia"), key, "farm-a", product);
        await PostAsync(As("ada"), key, "farm-a", product);

        Assert.Equal(2, (await BatchesOfAsync(product)).Count);
    }

    [Fact]
    public async Task REQ_BT_050_Key_too_long_is_refused()
    {
        var reply = await PostAsync(As("olivia"), new string('k', 201), "farm-a", "long-key");

        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_050_Create_with_no_key_still_works()
    {
        Assert.Equal(HttpStatusCode.Created, (await As("olivia").PostCreate()).StatusCode);
    }

    private static Task<HttpResponseMessage> PostAsync(ApiUser user, string key, string farm, string product)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/batches")
        {
            Content = JsonContent.Create(new { farmId = farm, product })
        };
        request.Headers.Add("Idempotency-Key", key);
        return user.Http.SendAsync(request);
    }
}
