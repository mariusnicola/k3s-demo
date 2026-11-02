using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;
using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>Shared with tests/smoke/probe so both prove the services the same way.</summary>
public static class RoundTrips
{
    /// <summary>The Service Bus trip, inside the limit.</summary>
    public static Task ServiceBus(string endpoint, string queue, CancellationToken token) =>
        Limited(() => ServiceBusTrip(endpoint, queue, token), token);

    /// <summary>The Cosmos trip, inside the limit.</summary>
    public static Task Cosmos(string endpoint, string databaseName, CancellationToken token) =>
        Limited(() => CosmosTrip(endpoint, databaseName, token), token);

    /// <summary>The Mongo trip, inside the limit.</summary>
    public static Task Mongo(string connection, string databaseName, CancellationToken token) =>
        Limited(() => MongoTrip(connection, databaseName, token), token);

    /// <summary>
    /// Each SDK takes the token but retries a first connection without looking at it, and a new client starts on
    /// the calling thread, so the trip runs apart from the caller and the limit is held here.
    /// A trip past the limit is abandoned and ends by itself.
    /// </summary>
    public static Task Limited(Func<Task> trip, CancellationToken token) => Apart(trip).WaitAsync(token);

    /// <summary>Returns at the yield, so the limit is attached before the trip's first line runs.</summary>
    private static async Task Apart(Func<Task> trip)
    {
        await Task.Yield();
        await trip();
    }

    private static async Task ServiceBusTrip(string endpoint, string queue, CancellationToken token)
    {
        var connection = $"Endpoint=sb://{endpoint};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
        var body = Guid.NewGuid().ToString();
        await using var client = new ServiceBusClient(connection);
        await using var sender = client.CreateSender(queue);
        await sender.SendMessageAsync(new ServiceBusMessage(body), token);
        await using var receiver = client.CreateReceiver(queue);

        // A failed earlier run can leave its message on the queue.
        while (true)
        {
            var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10), token)
                ?? throw new InvalidOperationException("message not received");
            await receiver.CompleteMessageAsync(message, token);
            if (message.Body.ToString() == body) return;
        }
    }

    private static async Task MongoTrip(string connection, string databaseName, CancellationToken token)
    {
        try
        {
            using var client = new MongoClient(connection);
            var probe = client.GetDatabase(databaseName).GetCollection<BsonDocument>("probe");
            var id = Guid.NewGuid().ToString();
            var key = Builders<BsonDocument>.Filter.Eq("_id", id);
            await probe.InsertOneAsync(new BsonDocument("_id", id), cancellationToken: token);
            if (await probe.Find(key).FirstOrDefaultAsync(token) is null) throw new InvalidOperationException("item not read back");
            await probe.DeleteOneAsync(key, token);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // The driver's text can hold the connection, and this one holds a password. The report gets the type alone.
            throw new InvalidOperationException($"mongo round trip failed: {failure.GetType().Name}");
        }
    }

    private static async Task CosmosTrip(string endpoint, string databaseName, CancellationToken token)
    {
        // The emulator's published well-known key. It does not enforce authentication.
        var connection = $"AccountEndpoint=https://{endpoint}/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;DisableServerCertificateValidation=True;";
        using var client = new CosmosClient(connection, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true
        });
        Database database = await client.CreateDatabaseIfNotExistsAsync(databaseName, cancellationToken: token);
        Container container = await database.CreateContainerIfNotExistsAsync("probe", "/id", cancellationToken: token);
        var id = Guid.NewGuid().ToString();
        var key = new PartitionKey(id);
        await container.CreateItemAsync(new { id }, key, cancellationToken: token);
        var stored = await container.ReadItemAsync<Newtonsoft.Json.Linq.JObject>(id, key, cancellationToken: token);
        if ((string?)stored.Resource["id"] != id) throw new InvalidOperationException("item not read back");
        await container.DeleteItemAsync<object>(id, key, cancellationToken: token);
    }
}
