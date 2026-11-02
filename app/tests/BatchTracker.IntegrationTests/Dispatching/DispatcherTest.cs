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

/// <summary>What the tests of DispatcherTests and its neighbours share.</summary>
public abstract class DispatcherTest(Stack stack) : StackTest(stack)
{
    protected static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    protected DispatcherHost? host;

    protected DispatcherHost Start(IWorkerClient? worker = null, IEventQueueFactory? queues = null)
    {
        host = new DispatcherHost(services =>
        {
            services.RemoveAll<IWorkerClient>().AddSingleton(_ => worker ?? Stack.Worker.NewClient());
            services.RemoveAll<DispatcherOptions>().AddSingleton(new DispatcherOptions
            {
                SweepInterval = TimeSpan.FromMilliseconds(100),
                WorkerProbeInterval = TimeSpan.FromMilliseconds(200)
            });
            if (queues is not null) services.RemoveAll<IEventQueueFactory>().AddSingleton(queues);
        });
        host.CreateClient();
        host.Leadership.Gain();
        return host;
    }

    public override async Task DisposeAsync()
    {
        if (host is not null) await host.DisposeAsync();
    }

    protected static async Task Until(Func<Task<bool>> condition, string what, TimeSpan? limit = null)
    {
        using var timer = new CancellationTokenSource(limit ?? Limit);
        while (!await condition())
        {
            if (timer.IsCancellationRequested) throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }

    protected Task<Batch> ReadAsync(Batch batch) =>
        Stack.Store.GetAsync(batch.FarmId, batch.Id, default).ContinueWith(t => t.Result!.Batch);

    protected async Task<Created> SettledAsync(Batch batch)
    {
        await Until(async () => (await ReadAsync(batch)).Pending is null, $"{batch.Id} settled");
        return await As("ada").Read(batch);
    }

    protected async Task DeadLetterAsync(BatchEvent message, string reason)
    {
        await using var sender = Stack.Bus.CreateSender(Env.Queue);
        await sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(message, JsonDefaults.Options)) { MessageId = message.EventId });
        await using var receiver = Stack.Bus.CreateReceiver(Env.Queue);
        var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        await receiver.DeadLetterMessageAsync(received, reason, "test");
    }

    protected static BatchEvent EventOf(Batch batch) => new(
        batch.Pending!.EventId, batch.Id, batch.FarmId, batch.Pending.TraceId, batch.State.ToString());

    protected async Task<JsonElement> StatusAsync() =>
        JsonDocument.Parse(await host!.CreateClient().GetStringAsync("/status")).RootElement;

    protected sealed class CountingWorker(IWorkerClient inner) : IWorkerClient
    {
        private readonly List<string> eventIds = [];

        public IReadOnlyList<string> EventIds
        {
            get { lock (eventIds) return eventIds.ToList(); }
        }

        public Task<WorkerResult> PostEventAsync(BatchEvent message, CancellationToken token)
        {
            lock (eventIds) eventIds.Add(message.EventId);
            return inner.PostEventAsync(message, token);
        }

        public Task<bool> ReadyAsync(CancellationToken token) => inner.ReadyAsync(token);
        public Task<ComplianceResult> RunNightlyAsync(CancellationToken token) => inner.RunNightlyAsync(token);
    }

    protected sealed class OutageWorker(IWorkerClient inner) : IWorkerClient
    {
        private volatile bool down = true;
        private int refusedCalls;

        public int RefusedCalls => Volatile.Read(ref refusedCalls);
        public void Recover() => down = false;

        public Task<WorkerResult> PostEventAsync(BatchEvent message, CancellationToken token)
        {
            if (!down) return inner.PostEventAsync(message, token);
            Interlocked.Increment(ref refusedCalls);
            return Task.FromResult(WorkerResult.Unavailable);
        }

        public Task<bool> ReadyAsync(CancellationToken token) => down ? Task.FromResult(false) : inner.ReadyAsync(token);
        public Task<ComplianceResult> RunNightlyAsync(CancellationToken token) => inner.RunNightlyAsync(token);
    }

    protected sealed class HangingWorker(IWorkerClient inner) : IWorkerClient
    {
        private volatile bool hang = true;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => hang = false;

        public async Task<WorkerResult> PostEventAsync(BatchEvent message, CancellationToken token)
        {
            if (!hang) return await inner.PostEventAsync(message, token);
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return WorkerResult.Done;
        }

        public Task<bool> ReadyAsync(CancellationToken token) => inner.ReadyAsync(token);
        public Task<ComplianceResult> RunNightlyAsync(CancellationToken token) => inner.RunNightlyAsync(token);
    }
}
