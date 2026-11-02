using System.Net;
using System.Net.Http.Json;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

public class WorkerTests(Stack stack) : StackTest(stack)
{
    private static BatchEvent EventOf(Batch batch) => new(
        batch.Pending!.EventId, batch.Id, batch.FarmId, batch.Pending.TraceId, batch.State.ToString());

    private Task<HttpResponseMessage> PostAsync(WorkerHost host, BatchEvent message)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(HttpWorkerClient.TokenHeader, Env.InternalToken);
        return client.PostAsJsonAsync("/events", message, JsonDefaults.Options);
    }

    [Fact]
    public async Task REQ_BT_009_Event_writes_the_record_and_clears_pending()
    {
        var created = await As("olivia").Create();

        var response = await PostAsync(Stack.Worker, EventOf(created.Batch));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var batch = (await As("olivia").Read(created.Batch)).Batch;
        Assert.Null(batch.Pending);
        Assert.NotEmpty(batch.Steps[0].RecordId);
        Assert.NotEmpty(batch.Steps[0].Hash);
        Assert.True(HashChain.Verify(batch));
    }

    [Fact]
    public async Task REQ_BT_010_Redelivered_event_changes_nothing()
    {
        var created = await As("olivia").Create();
        var message = EventOf(created.Batch);
        await PostAsync(Stack.Worker, message);
        var before = await As("olivia").Read(created.Batch);

        var response = await PostAsync(Stack.Worker, message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before.ETag, (await As("olivia").Read(created.Batch)).ETag);
    }

    [Fact]
    public async Task REQ_BT_010_Event_for_an_unknown_batch_is_200()
    {
        var created = await As("olivia").Create();
        var ghost = EventOf(created.Batch) with { BatchId = Guid.NewGuid().ToString() };

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Stack.Worker, ghost)).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_011_FailNext_answers_422_and_writes_nothing()
    {
        var created = await As("olivia").Create();
        await As("ada").FailNext(created.Batch);
        var before = await As("olivia").Read(created.Batch);

        var response = await PostAsync(Stack.Worker, EventOf(before.Batch));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(before.ETag, (await As("olivia").Read(created.Batch)).ETag);
    }

    [Fact]
    public async Task REQ_BT_009_Race_with_the_dispatcher_mark_rereads_and_still_records()
    {
        var created = await As("olivia").Create();
        var store = Stack.Store;
        await using var racing = new WorkerHost(services =>
            services.RemoveAll<IBatchStore>().AddSingleton<IBatchStore>(new MarkingStore(store)));

        var response = await PostAsync(racing, EventOf(created.Batch));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var batch = (await As("olivia").Read(created.Batch)).Batch;
        Assert.Null(batch.Pending);
        Assert.True(HashChain.Verify(batch));
    }

    [Fact]
    public async Task REQ_BT_021_A_batch_that_keeps_changing_answers_409()
    {
        var created = await As("olivia").Create();
        var store = Stack.Store;
        await using var racing = new WorkerHost(services =>
            services.RemoveAll<IBatchStore>().AddSingleton<IBatchStore>(new MarkingStore(store, raceEvery: true)));

        var response = await PostAsync(racing, EventOf(created.Batch));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_014_Worker_routes_refuse_a_missing_or_wrong_token()
    {
        var created = await As("olivia").Create();
        var none = Stack.Worker.CreateClient();
        var wrong = Stack.Worker.CreateClient();
        wrong.DefaultRequestHeaders.Add(HttpWorkerClient.TokenHeader, "wrong");

        foreach (var client in new[] { none, wrong })
        {
            var posted = await client.PostAsJsonAsync("/events", EventOf(created.Batch), JsonDefaults.Options);
            Assert.Equal(HttpStatusCode.Unauthorized, posted.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/nightly/run", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/ready")).StatusCode);
        }
        Assert.NotNull((await As("olivia").Read(created.Batch)).Batch.Pending);
    }

    [Fact]
    public async Task REQ_BT_014_Worker_healthz_is_anonymous_and_ready_answers_the_token()
    {
        Assert.Equal(HttpStatusCode.OK, (await Stack.Worker.CreateClient().GetAsync("/healthz")).StatusCode);
        Assert.True(await Stack.Worker.NewClient().ReadyAsync(default));
    }

    [Fact]
    public async Task REQ_BT_021_Client_reports_a_dead_worker_as_unavailable_not_as_refused()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
        var client = new HttpWorkerClient(http, new WorkerRoute(Env.InternalToken, null));
        var message = new BatchEvent("b:Created", "b", "farm-a", "t", "Created");

        Assert.Equal(WorkerResult.Unavailable, await client.PostEventAsync(message, default));
        Assert.False(await client.ReadyAsync(default));
    }

    /// <summary>A real Dispatcher write lands between the Worker's read and its replace.</summary>
    private sealed class MarkingStore(IBatchStore inner, bool raceEvery = false) : IBatchStore
    {
        private int raced;

        public async Task<Versioned?> ReplaceAsync(Batch batch, string etag, CancellationToken token)
        {
            if (Interlocked.Exchange(ref raced, 1) == 0 || raceEvery)
            {
                var current = (await inner.GetAsync(batch.FarmId, batch.Id, token))!;
                var marked = current.Batch with
                {
                    Pending = current.Batch.Pending! with { Status = PendingStatus.Sent, SentAt = DateTimeOffset.UtcNow }
                };
                await inner.ReplaceAsync(marked, current.ETag, token);
            }
            return await inner.ReplaceAsync(batch, etag, token);
        }

        public Task<Versioned?> CreateAsync(Batch batch, CancellationToken token) => inner.CreateAsync(batch, token);
        public Task<IReadOnlyList<Versioned>> GetManyAsync(IReadOnlyList<BatchKey> keys, CancellationToken token) => inner.GetManyAsync(keys, token);
        public Task<IReadOnlyList<Versioned?>> WriteManyAsync(IReadOnlyList<BundleWrite> writes, CancellationToken token) => inner.WriteManyAsync(writes, token);
        public Task<Versioned?> GetAsync(string farmId, string id, CancellationToken token) => inner.GetAsync(farmId, id, token);
        public Task<IReadOnlyList<Versioned>> ListAsync(BatchFilter filter, CancellationToken token) => inner.ListAsync(filter, token);
        public Task<IReadOnlyList<Versioned>> FindByPendingStatusAsync(string status, int max, CancellationToken token) =>
            inner.FindByPendingStatusAsync(status, max, token);
        public Task<IReadOnlyList<Versioned>> FindSentBeforeAsync(DateTimeOffset cutoff, int max, CancellationToken token) =>
            inner.FindSentBeforeAsync(cutoff, max, token);
        public Task<IReadOnlyList<Versioned>> MarkSentAsync(IReadOnlyList<Versioned> rows, DateTimeOffset at, CancellationToken token) =>
            inner.MarkSentAsync(rows, at, token);
        public Task<IReadOnlyList<Versioned>> FindInStateAsync(BatchState state, CancellationToken token) =>
            inner.FindInStateAsync(state, token);
        public Task<IReadOnlyList<Batch>> FindForReportsAsync(IReadOnlyList<string>? farms, CancellationToken token) =>
            inner.FindForReportsAsync(farms, token);
        public Task<LiveScan> FindForLiveAsync(CancellationToken token) => inner.FindForLiveAsync(token);
        public Task<bool> ReachableAsync(CancellationToken token) => inner.ReachableAsync(token);
    }
}
