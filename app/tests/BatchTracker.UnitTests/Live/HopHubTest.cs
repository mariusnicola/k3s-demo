using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Live;

namespace BatchTracker.UnitTests;

/// <summary>What the tests of HopHubTests and its neighbours share.</summary>
public abstract class HopHubTest
{
    protected static readonly Caller Everyone = new("ada", [], [Caller.AllFarms]);
    protected static readonly Caller FarmA = new("viewer", [], ["farm-a"]);

    protected static Hop NewHop(string farm = "farm-a", int number = 0) =>
        new($"t-{number}", "b", farm, HopNames.Api, "Created", DateTimeOffset.UnixEpoch, true);

    protected static SocketClient Connected(HopHub hub, Caller caller)
    {
        var client = hub.Connect(caller);
        Assert.True(client.Outbox.TryRead(out var snapshot));
        Assert.Equal("snapshot", JsonDocument.Parse(snapshot!).RootElement.GetProperty("type").GetString());
        return client;
    }

    protected static string[] Traces(byte[] frame) =>
        JsonDocument.Parse(frame).RootElement.GetProperty("hops").EnumerateArray().Select(e => e.GetProperty("traceId").GetString()!).ToArray();

    protected static Versioned Row(string id, long version, string farm = "farm-a", BatchState state = BatchState.Created, DateTimeOffset? createdAt = null) => new(
        new Batch { Id = id, FarmId = farm, Product = "p", State = state, CreatedAt = createdAt ?? DateTimeOffset.UnixEpoch, Version = version },
        $"\"{id}-{version}\"");

    /// <summary>The row as the write that created it leaves it: only that write adds a batch the hub has not seen.</summary>
    protected static Versioned New(Versioned row) => row with
    {
        Batch = row.Batch with { Pending = new PendingEvent { EventId = "e", TraceId = "t", Status = PendingStatus.Pending } }
    };

    protected static JsonElement Snapshot(HopHub hub, Caller caller)
    {
        hub.Connect(caller).Outbox.TryRead(out var frame);
        return JsonDocument.Parse(frame!).RootElement;
    }

    protected static int Created(JsonElement snapshot) => snapshot.GetProperty("summary").GetProperty("byState").GetProperty("Created").GetInt32();
}
