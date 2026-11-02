using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.IntegrationTests;

public class BatchRouteTests(Stack stack) : BatchRouteTest(stack)
{
    [Fact]
    public async Task REQ_BT_004_Create_returns_201_with_ETag_and_location()
    {
        var response = await As("olivia").PostCreate();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var batch = await ApiUser.ReadBatch(response);
        Assert.False(string.IsNullOrEmpty(ApiUser.ETagOf(response)));
        Assert.Equal($"/api/batches/farm-a/{batch.Id}", response.Headers.Location!.ToString());
        Assert.Equal(BatchState.Created, batch.State);
    }

    [Fact]
    public async Task REQ_BT_004_Create_stores_the_document_in_the_contract_shape()
    {
        var created = await As("olivia").Create();

        var doc = (await Stack.Batches.ReadItemAsync<JsonElement>(
            created.Batch.Id, new Microsoft.Azure.Cosmos.PartitionKey("farm-a"))).Resource;

        Assert.Equal("farm-a", doc.GetProperty("farmId").GetString());
        Assert.Equal("Created", doc.GetProperty("state").GetString());
        Assert.False(doc.GetProperty("failNext").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("reason").ValueKind);
        var pending = doc.GetProperty("pending");
        Assert.Equal($"{created.Batch.Id}:Created", pending.GetProperty("eventId").GetString());
        Assert.Equal("pending", pending.GetProperty("status").GetString());
        var step = doc.GetProperty("steps")[0];
        Assert.Equal("olivia", step.GetProperty("by").GetString());
        Assert.Equal("", step.GetProperty("recordId").GetString());
    }

    [Fact]
    public async Task REQ_BT_001_Each_route_walks_a_batch_to_Closed()
    {
        var olivia = As("olivia");
        var closed = await olivia.WalkTo(BatchState.Closed, As("ian"));

        Assert.Equal(BatchState.Closed, closed.Batch.State);
        Assert.True(HashChain.Verify(closed.Batch));
        Assert.Equal(6, closed.Batch.Steps.Count);
    }

    [Fact]
    public async Task REQ_BT_003_Failed_inspection_is_Rejected_with_reason()
    {
        var row = await As("olivia").WalkTo(BatchState.Dose2, As("ian"));

        var response = await As("ian").Inspect(row.Batch, false, "Cold chain broken", row.ETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var batch = await ApiUser.ReadBatch(response);
        Assert.Equal(BatchState.Rejected, batch.State);
        Assert.Equal("Cold chain broken", batch.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task REQ_BT_007_Failed_inspection_with_blank_reason_is_400(string reason)
    {
        var row = await As("olivia").WalkTo(BatchState.Dose2, As("ian"));

        await AssertProblem(await As("ian").Inspect(row.Batch, false, reason, row.ETag), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task REQ_BT_007_Inspect_without_passed_is_400()
    {
        var row = await As("olivia").WalkTo(BatchState.Dose2, As("ian"));

        await AssertProblem(await As("ian").Inspect(row.Batch, null, null, row.ETag), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task REQ_BT_005_Advance_without_If_Match_is_428_and_nothing_written()
    {
        var olivia = As("olivia");
        var row = await olivia.CreateSettled();

        await AssertProblem(await olivia.Advance(row.Batch, "Dose1", null), HttpStatusCode.PreconditionRequired);

        Assert.Equal(row.ETag, (await olivia.Read(row.Batch)).ETag);
    }

    [Fact]
    public async Task REQ_BT_005_Inspect_without_If_Match_is_428()
    {
        var row = await As("olivia").WalkTo(BatchState.Dose2, As("ian"));

        await AssertProblem(await As("ian").Inspect(row.Batch, true, null, null), HttpStatusCode.PreconditionRequired);
    }

    [Theory]
    [InlineData("Dose2")]
    [InlineData("Closed")]
    [InlineData("Created")]
    [InlineData("Rejected")]
    public async Task REQ_BT_007_Illegal_move_is_409(string to)
    {
        var olivia = As("olivia");
        var row = await olivia.CreateSettled();

        await AssertProblem(await olivia.Advance(row.Batch, to, row.ETag), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task REQ_BT_007_Inspect_outside_Dose2_is_409()
    {
        var row = await As("olivia").CreateSettled();

        await AssertProblem(await As("ian").Inspect(row.Batch, true, null, row.ETag), HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("Dose9")]
    [InlineData("dose1")]
    [InlineData("1")]
    [InlineData("")]
    public async Task REQ_BT_007_Unknown_target_state_is_400(string to)
    {
        var olivia = As("olivia");
        var row = await olivia.CreateSettled();

        await AssertProblem(await olivia.Advance(row.Batch, to, row.ETag), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task REQ_BT_007_Malformed_json_is_400()
    {
        var content = new StringContent("{ not json", Encoding.UTF8, "application/json");

        var response = await As("olivia").Http.PostAsync("/api/batches", content);

        await AssertProblem(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("", "Vaccine")]
    [InlineData("farm-a", "")]
    [InlineData("farm-a", "   ")]
    public async Task REQ_BT_007_Create_with_missing_field_is_400(string farmId, string product)
    {
        await AssertProblem(await As("olivia").PostCreate(farmId, product), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task REQ_BT_007_Create_with_product_over_the_limit_is_400()
    {
        await AssertProblem(await As("olivia").PostCreate(product: new string('x', 201)), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task REQ_BT_007_Create_on_unknown_farm_is_400_for_an_all_farms_user()
    {
        await AssertProblem(await As("ada").PostCreate("farm-unknown"), HttpStatusCode.BadRequest);
    }
}
