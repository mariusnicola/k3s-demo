using System.Net.Http.Json;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

/// <summary>The real Dispatcher host on the real queue. Only the election is replaced, by the signal it drives.</summary>
public sealed class DispatcherTests(Stack stack) : DispatcherTest(stack)
{
    [Fact]
    public async Task REQ_BT_009_Every_step_becomes_one_post_and_one_record()
    {
        var counting = new CountingWorker(Stack.Worker.NewClient());
        Start(counting);
        var ada = As("ada");

        var row = await SettledAsync((await ada.Create()).Batch);
        foreach (var next in new[] { "Dose1", "Dose2" })
        {
            (await ada.Advance(row.Batch, next, row.ETag)).EnsureSuccessStatusCode();
            row = await SettledAsync(row.Batch);
        }
        (await ada.Inspect(row.Batch, true, null, row.ETag)).EnsureSuccessStatusCode();
        row = await SettledAsync(row.Batch);
        foreach (var next in new[] { "Shipped", "Closed" })
        {
            (await ada.Advance(row.Batch, next, row.ETag)).EnsureSuccessStatusCode();
            row = await SettledAsync(row.Batch);
        }

        Assert.Equal(BatchState.Closed, row.Batch.State);
        Assert.Equal(6, row.Batch.Steps.Count);
        Assert.All(row.Batch.Steps, s => Assert.NotEmpty(s.RecordId));
        Assert.True(HashChain.Verify(row.Batch));
        Assert.Equal(row.Batch.Steps.Select(s => $"{row.Batch.Id}:{s.State}"), counting.EventIds);
    }

    [Fact]
    public async Task REQ_BT_009_The_widest_pack_goes_through_the_real_bus()
    {
        await using var queue = await new ServiceBusQueueFactory(Env.Bus, Env.Queue).ConnectAsync(default);
        var id = Guid.NewGuid().ToString();
        var widest = Enumerable.Range(0, 400)
            .Select(i => new BatchEvent($"{id}:Inspected", id, "farm-a", new string('t', 32), "Inspected", new string('c', 64))).ToList();

        await queue.SendAsync(widest, default);
        var pack = await queue.ReceiveAsync(TimeSpan.FromSeconds(10), default);

        Assert.Equal(400, pack.Count);
        foreach (var delivery in pack) await delivery.CompleteAsync(default);
    }

    [Fact]
    public async Task REQ_BT_020_A_lapsed_pack_comes_back_and_a_done_event_makes_no_second_record()
    {
        var ada = As("ada");
        var (done, failed) = ((await ada.Create()).Batch, (await ada.Create()).Batch);
        var worker = new ThrowsOnce(Stack.Worker.NewClient(), failed.Id);

        Start(worker);

        await SettledAsync(failed);
        var first = await SettledAsync(done);
        Assert.Equal(2, worker.Posts(done.Id));
        Assert.Single(first.Batch.Steps);
    }

    /// <summary>The first post of one batch throws, so its outcome is never recorded and the pack's lock lapses.</summary>
    private sealed class ThrowsOnce(IWorkerClient inner, string batchId) : IWorkerClient
    {
        private readonly List<string> posts = [];
        private int thrown;

        public int Posts(string id)
        {
            lock (posts) return posts.Count(p => p == id);
        }

        public Task<WorkerResult> PostEventAsync(BatchEvent message, CancellationToken token)
        {
            lock (posts) posts.Add(message.BatchId);
            if (message.BatchId == batchId && Interlocked.Increment(ref thrown) == 1) throw new InvalidOperationException("thrown once");
            return inner.PostEventAsync(message, token);
        }

        public Task<bool> ReadyAsync(CancellationToken token) => inner.ReadyAsync(token);
        public Task<ComplianceResult> RunNightlyAsync(CancellationToken token) => inner.RunNightlyAsync(token);
    }

    [Fact]
    public async Task REQ_BT_011_Worker_422_lands_in_dead_letters_blocks_and_replay_completes_it()
    {
        Start();
        var ada = As("ada");
        var batch = (await ada.Create()).Batch;
        await ada.FailNext(batch);

        await Until(async () => (await ReadAsync(batch)).IsBlocked(), "blocked");

        Assert.Equal("Worker refused the event.", (await ReadAsync(batch)).Pending!.Error);
        await Until(async () => await Stack.DeadLetterCountAsync() == 0, "dead letter completed");

        (await ada.Replay(batch)).EnsureSuccessStatusCode();

        var done = await SettledAsync(batch);
        Assert.False(done.Batch.FailNext);
        Assert.NotEmpty(done.Batch.Steps[^1].RecordId);
    }

    [Fact]
    public async Task REQ_BT_021_Worker_outage_does_not_dead_letter_a_good_event()
    {
        var worker = new OutageWorker(Stack.Worker.NewClient());
        Start(worker);
        var ada = As("ada");

        var batch = (await ada.Create()).Batch;
        await Until(() => Task.FromResult(worker.RefusedCalls >= new DispatcherOptions().WorkerFailuresToStop), "outage seen");
        var later = (await ada.Create()).Batch;
        await Task.Delay(600);

        Assert.Equal(PendingStatus.Pending, (await ReadAsync(later)).Pending!.Status);
        Assert.Equal(0, await Stack.DeadLetterCountAsync());

        worker.Recover();

        await SettledAsync(batch);
        await SettledAsync(later);
        Assert.Equal(0, await Stack.DeadLetterCountAsync());
    }

    [Fact]
    public async Task REQ_BT_020_Marked_event_with_no_message_is_resent_after_120_seconds()
    {
        var created = (await As("ada").Create()).Batch;
        var stored = (await Stack.Store.GetAsync(created.FarmId, created.Id, default))!;
        var lost = stored.Batch.Pending! with { Status = PendingStatus.Sent, SentAt = DateTimeOffset.UtcNow.AddSeconds(-121) };
        await Stack.Store.ReplaceAsync(stored.Batch with { Pending = lost }, stored.ETag, default);

        Start();

        await SettledAsync(created);
    }

    [Fact]
    public async Task REQ_BT_020_Recently_sent_event_is_not_resent()
    {
        var created = (await As("ada").Create()).Batch;
        var stored = (await Stack.Store.GetAsync(created.FarmId, created.Id, default))!;
        var sent = stored.Batch.Pending! with { Status = PendingStatus.Sent, SentAt = DateTimeOffset.UtcNow };
        var etag = (await Stack.Store.ReplaceAsync(stored.Batch with { Pending = sent }, stored.ETag, default))!.ETag;

        Start();
        await Task.Delay(1500);

        Assert.Equal(etag, (await Stack.Store.GetAsync(created.FarmId, created.Id, default))!.ETag);
    }

    [Fact]
    public async Task REQ_BT_010_Dead_letter_for_a_finished_event_changes_nothing()
    {
        var ada = As("ada");
        var created = (await ada.Create()).Batch;
        var finished = await ada.Settle(created);
        await DeadLetterAsync(EventOf(created), "poison");

        Start();

        await Until(async () => await Stack.DeadLetterCountAsync() == 0, "dead letter completed");
        Assert.Equal(finished.ETag, (await ada.Read(created)).ETag);
    }

    [Fact]
    public async Task REQ_BT_021_Dead_letter_not_refused_by_the_worker_returns_to_pending_and_completes()
    {
        var worker = new OutageWorker(Stack.Worker.NewClient());
        var created = (await As("ada").Create()).Batch;
        var stored = (await Stack.Store.GetAsync(created.FarmId, created.Id, default))!;
        var sent = stored.Batch.Pending! with { Status = PendingStatus.Sent, SentAt = DateTimeOffset.UtcNow };
        await Stack.Store.ReplaceAsync(stored.Batch with { Pending = sent }, stored.ETag, default);
        await DeadLetterAsync(EventOf(created), "MaxDeliveryCountExceeded");
        worker.Recover();

        Start(worker);

        var done = await SettledAsync(created);
        Assert.NotEmpty(done.Batch.Steps[^1].RecordId);
        Assert.False(done.Batch.IsBlocked());
    }
}
