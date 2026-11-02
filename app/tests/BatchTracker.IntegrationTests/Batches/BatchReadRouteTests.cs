using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.IntegrationTests;

/// <summary>Reading one batch and listing them.</summary>
public class BatchReadRouteTests(Stack stack) : BatchRouteTest(stack)
{
    [Fact]
    public async Task REQ_BT_007_Unknown_batch_is_404()
    {
        var olivia = As("olivia");
        var ghost = (await olivia.Create()).Batch with { Id = Guid.NewGuid().ToString() };

        await AssertProblem(await olivia.Get(ghost), HttpStatusCode.NotFound);
        await AssertProblem(await olivia.Advance(ghost, "Dose1", "\"x\""), HttpStatusCode.NotFound);
        await AssertProblem(await As("ada").Replay(ghost), HttpStatusCode.NotFound);
        await AssertProblem(await As("ada").FailNext(ghost), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task REQ_BT_007_Id_that_is_not_a_guid_is_404()
    {
        var response = await As("olivia").Http.GetAsync("/api/batches/farm-a/not-a-guid");

        await AssertProblem(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task REQ_BT_007_Get_returns_the_batch_with_ETag()
    {
        var olivia = As("olivia");
        var created = await olivia.Create();

        var response = await olivia.Get(created.Batch);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(created.ETag, ApiUser.ETagOf(response));
        Assert.Equal(created.Batch.Id, (await ApiUser.ReadBatch(response)).Id);
    }

    [Fact]
    public async Task REQ_BT_007_List_filters_by_state_and_farm_newest_first()
    {
        var ada = As("ada");
        var first = await ada.CreateSettled("farm-a");
        var second = await ada.Create("farm-a");
        await ada.Create("farm-b");

        var all = await ada.Http.GetFromJsonAsync<List<Batch>>("/api/batches?farmId=farm-a", JsonDefaults.Options);
        var created = await ada.Http.GetFromJsonAsync<List<Batch>>("/api/batches?state=Created", JsonDefaults.Options);

        Assert.Equal([second.Batch.Id, first.Batch.Id], all!.Select(b => b.Id));
        Assert.Equal(3, created!.Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("201")]
    public async Task REQ_BT_007_Limit_out_of_bounds_is_400(string limit)
    {
        await AssertProblem(await As("ada").Http.GetAsync($"/api/batches?limit={limit}"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task REQ_BT_007_Limit_that_is_not_a_number_is_400()
    {
        await AssertProblem(await As("ada").Http.GetAsync("/api/batches?limit=abc"), HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(200, 3)]
    public async Task REQ_BT_007_Limit_bounds_are_accepted(int limit, int expected)
    {
        var ada = As("ada");
        for (var i = 0; i < 3; i++) await ada.Create();

        var rows = await ada.Http.GetFromJsonAsync<List<Batch>>($"/api/batches?limit={limit}", JsonDefaults.Options);

        Assert.Equal(expected, rows!.Count);
    }

    [Fact]
    public async Task REQ_BT_007_Unknown_state_filter_is_400()
    {
        await AssertProblem(await As("ada").Http.GetAsync("/api/batches?state=Nope"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task REQ_BT_007_Empty_list_is_an_empty_array()
    {
        var rows = await As("ada").Http.GetFromJsonAsync<List<Batch>>("/api/batches", JsonDefaults.Options);

        Assert.Empty(rows!);
    }

    [Fact]
    public async Task REQ_BT_007_Unknown_farm_lists_empty_for_an_all_farms_user()
    {
        var rows = await As("ada").Http.GetFromJsonAsync<List<Batch>>("/api/batches?farmId=farm-unknown", JsonDefaults.Options);

        Assert.Empty(rows!);
    }
}
