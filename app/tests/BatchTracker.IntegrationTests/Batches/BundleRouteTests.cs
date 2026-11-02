using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.IntegrationTests;

/// <summary>The bundle route against the real API and store.</summary>
public sealed class BundleRouteTests(Stack stack) : StackTest(stack)
{
    private static object Create(string key) => new { kind = BundleKinds.Create, farmId = "farm-a", product = "Vaccine", key };

    private static async Task<JsonElement> ResultsOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("results");
    }

    private Task<HttpResponseMessage> Post(string? user, params object[] items) =>
        new ApiUser(Stack, user).Http.PostAsJsonAsync("/api/batches/bundle", new { items });

    [Fact]
    public async Task REQ_BT_031_A_create_and_a_bad_item_answer_apart_and_the_create_stands()
    {
        var results = await ResultsOf(await Post("ada", Create(Guid.NewGuid().ToString()), new { kind = "melt" }));

        Assert.Equal(201, results[0].GetProperty("status").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, results[0].GetProperty("etag").ValueKind);
        Assert.Equal(400, results[1].GetProperty("status").GetInt32());
        var id = results[0].GetProperty("batch").GetProperty("id").GetString()!;
        Assert.NotNull(await Stack.Store.GetAsync("farm-a", id, default));
    }

    [Fact]
    public async Task REQ_BT_031_A_step_advances_at_its_if_match_and_a_stale_one_answers_412()
    {
        var row = await As("olivia").CreateSettled();
        var step = new { kind = BundleKinds.Advance, farmId = row.Batch.FarmId, id = row.Batch.Id, to = nameof(BatchState.Dose1), ifMatch = row.ETag };

        var first = await ResultsOf(await Post("ada", step));
        var second = await ResultsOf(await Post("ada", step));

        Assert.Equal(200, first[0].GetProperty("status").GetInt32());
        Assert.Equal(412, second[0].GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("ada", HttpStatusCode.OK)]
    [InlineData("olivia", HttpStatusCode.OK)]
    [InlineData("ian", HttpStatusCode.Forbidden)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    public async Task REQ_BT_048_The_bundle_route_answers_each_role(string? user, HttpStatusCode expected)
    {
        Assert.Equal(expected, (await Post(user)).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_031_An_empty_bundle_is_no_work_and_past_the_limit_is_refused()
    {
        Assert.Equal(0, (await ResultsOf(await Post("ada"))).GetArrayLength());

        var over = Enumerable.Range(0, BatchBundle.MaxItems + 1).Select(_ => (object)new { kind = "melt" }).ToArray();

        Assert.Equal(HttpStatusCode.BadRequest, (await Post("ada", over)).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_031_One_store_write_refuses_a_lost_if_match_and_a_duplicate_and_saves_the_rest()
    {
        var stamp = new Stamp("ada", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var moved = await As("olivia").CreateSettled();
        var kept = await As("olivia").CreateSettled();
        var held = await As("olivia").CreateSettled();
        var fresh = BatchMachine.Create(Guid.NewGuid().ToString(), "farm-a", "Vaccine", stamp);
        var taken = BatchMachine.Create(held.Batch.Id, "farm-a", "Vaccine", stamp with { TraceId = Guid.NewGuid().ToString("N") });

        var saved = await Stack.Store.WriteManyAsync(
        [
            new BundleWrite(BatchMachine.Advance(moved.Batch, BatchState.Dose1, stamp with { TraceId = Guid.NewGuid().ToString("N") }).Batch!, "\"999\""),
            new BundleWrite(BatchMachine.Advance(kept.Batch, BatchState.Dose1, stamp with { TraceId = Guid.NewGuid().ToString("N") }).Batch!, kept.ETag),
            new BundleWrite(fresh, null),
            new BundleWrite(taken, null)
        ], default);

        Assert.Null(saved[0]);
        Assert.Equal(BatchState.Dose1, saved[1]!.Batch.State);
        Assert.Equal(fresh.Id, saved[2]!.Batch.Id);
        Assert.Null(saved[3]);
        Assert.Equal(BatchState.Created, (await Stack.Store.GetAsync("farm-a", moved.Batch.Id, default))!.Batch.State);
    }

    [Fact]
    public async Task REQ_BT_031_The_line_shows_its_bundle_figures_to_any_signed_in_caller_and_no_one_else()
    {
        await using var line = new LineHost(Stack.Api);
        var client = line.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/line/bundles")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", new TokenIssuer(Env.PrivateKey, TimeProvider.System).Issue(DemoUsers.MonitorMachine));
        var figures = JsonDocument.Parse(await client.GetStringAsync("/line/bundles")).RootElement;

        Assert.False(figures.GetProperty("failing").GetBoolean());
    }
}
