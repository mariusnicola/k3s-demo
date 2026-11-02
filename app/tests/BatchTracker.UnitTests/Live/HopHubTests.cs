using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Live;

namespace BatchTracker.UnitTests;

public class HopHubTests : HopHubTest
{
    [Fact]
    public void REQ_BT_050_A_hop_sent_again_after_a_lost_reply_is_held_once()
    {
        var hub = new HopHub(TimeProvider.System);
        var client = Connected(hub, Everyone);

        hub.Add(NewHop(number: 7));
        hub.Add(NewHop(number: 7));
        hub.Add(NewHop(number: 8));
        hub.Tick();

        Assert.True(client.Outbox.TryRead(out var frame));
        Assert.Equal(["t-7", "t-8"], Traces(frame!));
    }

    [Fact]
    public void REQ_BT_028_Tick_broadcasts_new_hops_as_one_typed_frame()
    {
        var hub = new HopHub(TimeProvider.System);
        var client = Connected(hub, Everyone);
        hub.Add(NewHop(number: 1));
        hub.Add(NewHop(number: 2));

        hub.Tick();

        Assert.True(client.Outbox.TryRead(out var frame));
        Assert.Equal(["t-1", "t-2"], Traces(frame!));
    }

    [Fact]
    public void REQ_BT_028_Hop_json_uses_the_contract_field_names()
    {
        var hub = new HopHub(TimeProvider.System);
        var client = Connected(hub, Everyone);
        hub.Add(NewHop());

        hub.Tick();

        client.Outbox.TryRead(out var frame);
        var hop = JsonDocument.Parse(frame!).RootElement.GetProperty("hops")[0];
        Assert.Equal(
            ["traceId", "batchId", "farmId", "hop", "state", "at", "ok"],
            hop.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void REQ_BT_028_Hops_go_out_once()
    {
        var hub = new HopHub(TimeProvider.System);
        var client = Connected(hub, Everyone);
        hub.Add(NewHop());

        hub.Tick();
        hub.Tick();

        Assert.True(client.Outbox.TryRead(out _));
        Assert.False(client.Outbox.TryRead(out _));
    }

    [Fact]
    public void REQ_BT_016_Client_sees_only_hops_of_its_farms()
    {
        var hub = new HopHub(TimeProvider.System);
        var client = Connected(hub, FarmA);
        hub.Add(NewHop("farm-b", 1));
        hub.Add(NewHop("farm-a", 2));

        hub.Tick();

        client.Outbox.TryRead(out var frame);
        Assert.Equal(["t-2"], Traces(frame!));
    }

    [Fact]
    public void REQ_BT_016_History_on_connect_is_filtered_too()
    {
        var hub = new HopHub(TimeProvider.System);
        hub.Add(NewHop("farm-b", 1));
        hub.Add(NewHop("farm-a", 2));

        var client = hub.Connect(FarmA);

        client.Outbox.TryRead(out var frame);
        Assert.Equal(["t-2"], Traces(frame!));
    }

    [Fact]
    public void REQ_BT_016_Client_with_no_visible_hops_gets_no_frame()
    {
        var hub = new HopHub(TimeProvider.System);
        var client = Connected(hub, FarmA);
        hub.Add(NewHop("farm-b"));

        hub.Tick();

        Assert.False(client.Outbox.TryRead(out _));
    }

    [Fact]
    public void REQ_BT_028_Hub_keeps_only_the_last_500()
    {
        var hub = new HopHub(TimeProvider.System);
        for (var i = 0; i < HopHub.Keep + 25; i++) hub.Add(NewHop(number: i));

        var client = hub.Connect(Everyone);

        client.Outbox.TryRead(out var frame);
        var traces = Traces(frame!);
        Assert.Equal(HopHub.Keep, traces.Length);
        Assert.Equal("t-25", traces[0]);
    }

    /// <summary>A throttled pod stalls for a second at a time. A healthy client is one frame-rate-second behind then, not slow.</summary>
    [Fact]
    public void REQ_BT_028_A_second_of_frames_at_rate_5_keeps_a_client_that_has_not_read_them_yet()
    {
        const int framesInASecond = 30;
        var hub = new HopHub(TimeProvider.System);
        var client = Connected(hub, Everyone);

        for (var i = 0; i < framesInASecond; i++)
        {
            hub.Add(NewHop(number: i));
            hub.Tick();
        }

        Assert.Equal(1, hub.ClientCount);
        Assert.Equal(framesInASecond, client.Outbox.Count);
    }

    [Fact]
    public void REQ_BT_028_Slow_client_is_dropped_and_others_keep_receiving()
    {
        var hub = new HopHub(TimeProvider.System);
        var slow = hub.Connect(Everyone);
        var fast = Connected(hub, Everyone);

        const int chunk = 64 * 1024;
        for (var i = 0; i <= SocketRules.MaxBacklogBytes / chunk; i++) slow.TrySend(new byte[chunk]);
        for (var i = 0; i < chunk; i++) slow.TrySend(new byte[1]);

        hub.Add(NewHop());
        hub.Tick();

        Assert.True(fast.Outbox.TryRead(out _));
        Assert.Equal(1, hub.ClientCount);
        Assert.False(slow.TrySend([]));
    }

    [Fact]
    public void REQ_BT_028_A_first_frame_past_the_limit_still_goes_out()
    {
        var client = new SocketClient(Everyone);

        Assert.True(client.TrySend(new byte[SocketRules.MaxBacklogBytes + 1]));
        Assert.False(client.TrySend(new byte[1]));
    }

    [Fact]
    public void REQ_BT_028_Disconnected_client_leaves_the_hub()
    {
        var hub = new HopHub(TimeProvider.System);
        var client = Connected(hub, Everyone);

        hub.Disconnect(client);

        Assert.Equal(0, hub.ClientCount);
    }
}
