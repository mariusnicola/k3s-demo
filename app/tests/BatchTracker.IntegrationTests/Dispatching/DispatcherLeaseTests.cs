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

/// <summary>Leadership, the status route, and what a lost connection does.</summary>
public sealed class DispatcherLeaseTests(Stack stack) : DispatcherTest(stack)
{
    [Fact]
    public async Task REQ_BT_010_Duplicate_messages_change_nothing()
    {
        var ada = As("ada");
        var created = (await ada.Create()).Batch;
        Start();
        var finished = await SettledAsync(created);
        var message = EventOf(created);

        await using var sender = Stack.Bus.CreateSender(Env.Queue);
        for (var i = 0; i < 3; i++)
            await sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(message, JsonDefaults.Options)) { MessageId = message.EventId });
        await using var receiver = Stack.Bus.CreateReceiver(Env.Queue);
        await Until(async () => await receiver.PeekMessageAsync() is null, "duplicates consumed");

        Assert.Equal(finished.ETag, (await ada.Read(created)).ETag);
        Assert.Equal(0, await Stack.DeadLetterCountAsync());
    }

    [Fact]
    public async Task REQ_BT_019_Lost_lease_mid_delivery_closes_and_a_new_term_finishes_the_event()
    {
        var worker = new HangingWorker(Stack.Worker.NewClient());
        var dispatcher = Start(worker);
        var created = (await As("ada").Create()).Batch;
        await worker.Reached.Task.WaitAsync(Limit);

        dispatcher.Leadership.Lose();

        await Until(async () =>
        {
            var status = await StatusAsync();
            return !status.GetProperty("leader").GetBoolean() && !status.GetProperty("connected").GetBoolean();
        }, "stepped down", TimeSpan.FromSeconds(5));
        Assert.NotNull((await ReadAsync(created)).Pending);

        worker.Release();
        dispatcher.Leadership.Gain();

        // The unsettled message returns when its 30 s lock ends, or the stale sweep covers it.
        await Until(async () => (await ReadAsync(created)).Pending is null, "settled in the new term", TimeSpan.FromSeconds(150));
        Assert.Equal(0, await Stack.DeadLetterCountAsync());
    }

    [Fact]
    public async Task REQ_BT_018_Status_reports_leader_and_connected_on_each_side_of_the_lease()
    {
        var dispatcher = Start();

        await Until(async () => (await StatusAsync()).GetProperty("connected").GetBoolean(), "connected");
        Assert.True((await StatusAsync()).GetProperty("leader").GetBoolean());

        dispatcher.Leadership.Lose();

        await Until(async () => !(await StatusAsync()).GetProperty("leader").GetBoolean(), "not leader", TimeSpan.FromSeconds(5));
        Assert.False((await StatusAsync()).GetProperty("connected").GetBoolean());
        Assert.Equal(System.Net.HttpStatusCode.OK, (await dispatcher.CreateClient().GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_059_The_leader_exports_its_stage_and_the_pending_count_and_a_standby_stops()
    {
        var dispatcher = Start();
        var scrape = dispatcher.CreateClient();
        await Until(async () => (await scrape.GetStringAsync("/metrics")).Contains("\nbt_events_pending "), "pending count", TimeSpan.FromSeconds(12));
        Assert.Contains("bt_stage_seconds_count{stage=\"store\"}", await scrape.GetStringAsync("/metrics"));

        dispatcher.Leadership.Lose();

        await Until(async () => !(await scrape.GetStringAsync("/metrics")).Contains("bt_events_pending"), "figure cleared", TimeSpan.FromSeconds(12));
    }

    /// <summary>Monitor parses this field by name, so it is read here through the real route.</summary>
    [Fact]
    public async Task REQ_BT_019_Status_carries_the_sweep_figure_only_during_a_term()
    {
        var dispatcher = Start();

        await Until(async () => (await StatusAsync()).GetProperty("connected").GetBoolean(), "connected");
        Assert.True((await StatusAsync()).GetProperty("secondsSinceSweep").GetDouble() >= 0);

        dispatcher.Leadership.Lose();

        await Until(async () => !(await StatusAsync()).GetProperty("leader").GetBoolean(), "not leader", TimeSpan.FromSeconds(5));
        Assert.False((await StatusAsync()).TryGetProperty("secondsSinceSweep", out var figure)
            && figure.ValueKind != System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task REQ_BT_020_Unreachable_bus_leaves_leader_not_connected_and_nothing_sent()
    {
        var down = new ServiceBusQueueFactory(Env.Bus.Replace("5682", "5999"), Env.Queue);
        var created = (await As("ada").Create()).Batch;

        Start(queues: down);
        await Until(async () => (await StatusAsync()).GetProperty("leader").GetBoolean(), "leader");
        await Task.Delay(1000);

        var status = await StatusAsync();
        Assert.False(status.GetProperty("connected").GetBoolean());
        Assert.Equal(PendingStatus.Pending, (await ReadAsync(created)).Pending!.Status);
    }
}
