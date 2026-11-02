using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using BatchTracker.Application;
using BatchTracker.Infrastructure;

namespace BatchTracker.UnitTests;

public sealed class FakeSender : ServiceBusSender
{
    public List<ServiceBusMessage> Sent { get; } = [];

    public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class FakeReceiver : ServiceBusReceiver
{
    public ConcurrentQueue<ServiceBusReceivedMessage> Waiting { get; } = new();
    public ConcurrentQueue<ServiceBusReceivedMessage> Completed { get; } = new();

    public static BatchEvent Event(string id) => new($"{id}:Created", id, "farm-a", "t", "Created", $"c-{id}");

    public static ServiceBusReceivedMessage Pack(params string[] ids) => ServiceBusModelFactory.ServiceBusReceivedMessage(
        BinaryData.FromObjectAsJson(ids.Select(Event).ToArray(), JsonDefaults.Options), subject: ServiceBusEventQueue.PackSubject);

    public override async Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(
        int maxMessages, TimeSpan? maxWaitTime = null, CancellationToken cancellationToken = default)
    {
        if (Waiting.TryDequeue(out var message)) return [message];
        await Task.Delay(maxWaitTime!.Value, cancellationToken);
        return [];
    }

    public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
    {
        Completed.Enqueue(message);
        return Task.CompletedTask;
    }
}

public sealed class FakeBusClient(FakeSender sender, FakeReceiver receiver, FakeReceiver dead) : ServiceBusClient
{
    public override ServiceBusSender CreateSender(string queueOrTopicName) => sender;
    public override ServiceBusReceiver CreateReceiver(string queueName) => receiver;
    public override ServiceBusReceiver CreateReceiver(string queueName, ServiceBusReceiverOptions options) => dead;
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
