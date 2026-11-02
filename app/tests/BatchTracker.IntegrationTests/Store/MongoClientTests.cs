using BatchTracker.Infrastructure;

namespace BatchTracker.IntegrationTests;

/// <summary>Reads the settings only, so it needs no server.</summary>
public sealed class MongoClientTests
{
    [Fact]
    public void REQ_BT_009_The_Mongo_pool_stays_at_100_whatever_the_connection_string_says()
    {
        using var client = MongoBatchStore.CreateClient("mongodb://127.0.0.1:1/?maxPoolSize=5");

        Assert.Equal(100, client.Settings.MaxConnectionPoolSize);
    }
}
