using Azure.Messaging.ServiceBus;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Infrastructure;
using static BatchTracker.UnitTests.FakeReceiver;

namespace BatchTracker.UnitTests;

/// <summary>The real queue over the SDK's own types, so what a bus message holds and when it settles are read, not assumed.</summary>
public sealed class ServiceBusPackTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(20);

    private readonly FakeSender sender = new();
    private readonly FakeReceiver receiver = new();
    private readonly FakeReceiver dead = new();

    private ServiceBusEventQueue NewQueue() => new(new FakeBusClient(sender, receiver, dead), "batch-events");

    [Fact]
    public async Task REQ_BT_009_A_list_of_events_goes_as_one_message_marked_as_a_pack()
    {
        await using var queue = NewQueue();

        await queue.SendAsync([Event("a"), Event("b"), Event("c")], default);

        var message = Assert.Single(sender.Sent);
        Assert.Equal(ServiceBusEventQueue.PackSubject, message.Subject);
        var events = message.Body.ToObjectFromJson<BatchEvent[]>(JsonDefaults.Options)!;
        Assert.Equal(["a", "b", "c"], events.Select(e => e.BatchId));
        Assert.Equal("c-b", events[1].CorrelationId);
    }

    [Fact]
    public async Task REQ_BT_009_A_bus_message_gives_each_of_its_events()
    {
        await using var queue = NewQueue();
        receiver.Waiting.Enqueue(Pack("a", "b", "c"));

        var pack = await queue.ReceiveAsync(Wait, default);

        Assert.Equal(["a", "b", "c"], pack.Select(d => d.Event.BatchId));
    }

    [Fact]
    public async Task REQ_BT_009_One_receive_takes_one_message()
    {
        await using var queue = NewQueue();
        receiver.Waiting.Enqueue(Pack("a"));
        receiver.Waiting.Enqueue(Pack("b"));

        Assert.Equal("a", Assert.Single(await queue.ReceiveAsync(Wait, default)).Event.BatchId);
        Assert.Single(receiver.Waiting);
    }

    [Fact]
    public async Task REQ_BT_009_The_bus_message_completes_on_its_last_event_and_not_before()
    {
        await using var queue = NewQueue();
        receiver.Waiting.Enqueue(Pack("a", "b", "c"));
        var pack = await queue.ReceiveAsync(Wait, default);

        await pack[0].CompleteAsync(default);
        await pack[2].CompleteAsync(default);
        Assert.Empty(receiver.Completed);

        await pack[1].CompleteAsync(default);
        Assert.Single(receiver.Completed);
    }

    [Fact]
    public async Task REQ_BT_009_A_message_with_no_subject_reads_as_a_pack_of_one()
    {
        await using var queue = NewQueue();
        receiver.Waiting.Enqueue(ServiceBusModelFactory.ServiceBusReceivedMessage(BinaryData.FromObjectAsJson(Event("old"), JsonDefaults.Options)));

        var pack = await queue.ReceiveAsync(Wait, default);

        Assert.Equal("old", Assert.Single(pack).Event.BatchId);
        await pack[0].CompleteAsync(default);
        Assert.Single(receiver.Completed);
    }
}
