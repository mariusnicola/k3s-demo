using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>A batch past the Dose 2 window cannot be made through the API, so it is written straight to the store.</summary>
public static class OverdueBatch
{
    public const string Farm = "farm-smoke";

    /// <summary>By environment: a failed probe prints its arguments, and the connection holds a password.</summary>
    private static IMongoCollection<BsonDocument> Batches()
    {
        var connection = Environment.GetEnvironmentVariable("PROBE_MONGO")
            ?? throw new InvalidOperationException("PROBE_MONGO is not set");
        return new MongoClient(connection).GetDatabase("batch-tracker").GetCollection<BsonDocument>("batches");
    }

    private static FilterDefinition<BsonDocument> Key(string farm, string id) =>
        Builders<BsonDocument>.Filter.Eq("_id", $"{farm}/{id}");

    public static async Task Seed(string id, CancellationToken token)
    {
        var doseAt = DateTimeOffset.UtcNow.AddDays(-400).ToString("O");
        var step = new BsonDocument
        {
            ["state"] = "Dose1", ["at"] = doseAt, ["by"] = "smoke", ["traceId"] = id,
            ["recordId"] = "smoke", ["hash"] = "smoke", ["previousHash"] = ""
        };
        var batch = new BsonDocument
        {
            ["_id"] = $"{Farm}/{id}", ["id"] = id, ["farmId"] = Farm, ["product"] = "smoke", ["state"] = "Dose1",
            ["createdAt"] = doseAt, ["reason"] = BsonNull.Value, ["failNext"] = false, ["flags"] = new BsonArray(),
            ["steps"] = new BsonArray { step }, ["pending"] = BsonNull.Value, ["version"] = 1,
            ["writtenAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await Batches().InsertOneAsync(batch, cancellationToken: token);
    }

    public static async Task AssertFlags(string id, int expected, CancellationToken token)
    {
        var stored = await Batches().Find(Key(Farm, id)).FirstOrDefaultAsync(token)
            ?? throw new InvalidOperationException($"{id} is not in the store");
        var flags = stored["flags"].AsBsonArray.Count(f => f["type"].AsString == "Dose2Overdue");
        if (flags != expected) throw new InvalidOperationException($"{flags} Dose2Overdue flags, expected {expected}");
    }

    public static async Task Delete(string id, string farm, CancellationToken token)
    {
        var deleted = await Batches().DeleteOneAsync(Key(farm, id), token);
        if (deleted.DeletedCount != 1) throw new InvalidOperationException($"{farm}/{id} was not in the store");
    }
}
