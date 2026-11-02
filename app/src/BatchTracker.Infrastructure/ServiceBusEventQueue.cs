using System.Text.Json;
using Azure.Messaging.ServiceBus;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;

namespace BatchTracker.Infrastructure;

public sealed class ServiceBusQueueFactory(string connectionString, string queueName) : IEventQueueFactory
{
    public async Task<IEventQueue> ConnectAsync(CancellationToken token)
    {
        var client = new ServiceBusClient(connectionString, new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions
            {
                MaxRetries = RetryRules.MaxAttempts - 1,
                Mode = ServiceBusRetryMode.Exponential,
                Delay = RetryRules.FirstDelay,
                MaxDelay = RetryRules.MaxDelay,
                TryTimeout = TimeSpan.FromSeconds(10)
            }
        });
        var queue = new ServiceBusEventQueue(client, queueName);
        try
        {
            await queue.ProbeAsync(token);
            return queue;
        }
        catch
        {
            await queue.DisposeAsync();
            throw;
        }
    }
}

public sealed class ServiceBusEventQueue(ServiceBusClient client, string queueName) : IEventQueue
{
    /// <summary>Absent on a message of one event, written before packs.</summary>
    public const string PackSubject = "pack";

    private readonly ServiceBusSender sender = client.CreateSender(queueName);
    private readonly ServiceBusReceiver receiver = client.CreateReceiver(queueName);
    private readonly ServiceBusReceiver deadLetterReceiver = client.CreateReceiver(
        queueName, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

    public async Task ProbeAsync(CancellationToken token) => await receiver.PeekMessageAsync(cancellationToken: token);

    /// <summary>One message, so a round costs the bus one send. The widest list fits the size limit, so nothing here splits one.</summary>
    public Task SendAsync(IReadOnlyList<BatchEvent> messages, CancellationToken token) =>
        sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(messages, JsonDefaults.Options)) { Subject = PackSubject }, token);

    public Task<IReadOnlyList<IDelivery>> ReceiveAsync(TimeSpan wait, CancellationToken token) =>
        ReceiveFromAsync(receiver, 1, wait, token);

    public Task<IReadOnlyList<IDelivery>> ReceiveDeadLettersAsync(int max, TimeSpan wait, CancellationToken token) =>
        ReceiveFromAsync(deadLetterReceiver, max, wait, token);

    /// <summary>Closing the client closes every link in it, so a lost lease stops all traffic at once.</summary>
    public async ValueTask DisposeAsync() => await client.DisposeAsync();

    private static async Task<IReadOnlyList<IDelivery>> ReceiveFromAsync(
        ServiceBusReceiver from, int max, TimeSpan wait, CancellationToken token)
    {
        var messages = await from.ReceiveMessagesAsync(max, wait, token);
        return messages.SelectMany(m => Unpack(from, m)).ToList();
    }

    private static IEnumerable<IDelivery> Unpack(ServiceBusReceiver from, ServiceBusReceivedMessage message)
    {
        var events = message.Subject == PackSubject
            ? message.Body.ToObjectFromJson<BatchEvent[]>(JsonDefaults.Options)!
            : [message.Body.ToObjectFromJson<BatchEvent>(JsonDefaults.Options)!];
        var pack = new Pack(from, message, events.Length);
        return events.Select(e => new PackedEvent(pack, e));
    }

    /// <summary>The count of events left. No failure settles a pack: a failed event is on its row, or the lock lapses.</summary>
    private sealed class Pack(ServiceBusReceiver from, ServiceBusReceivedMessage message, int events)
    {
        private int left = events;

        public Task CountDownAsync(CancellationToken token) =>
            Interlocked.Decrement(ref left) == 0 ? from.CompleteMessageAsync(message, token) : Task.CompletedTask;
    }

    private sealed class PackedEvent(Pack pack, BatchEvent message) : IDelivery
    {
        public BatchEvent Event => message;
        public Task CompleteAsync(CancellationToken token) => pack.CountDownAsync(token);
    }
}
