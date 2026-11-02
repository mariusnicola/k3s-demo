using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.IntegrationTests;

/// <summary>If-Match, pending events, blocked batches and concurrent writes.</summary>
public class BatchRaceRouteTests(Stack stack) : BatchRouteTest(stack)
{
    [Fact]
    public async Task REQ_BT_006_Stale_If_Match_is_412_and_nothing_written()
    {
        var olivia = As("olivia");
        var row = await olivia.CreateSettled();

        await AssertProblem(await olivia.Advance(row.Batch, "Dose1", StaleETag), HttpStatusCode.PreconditionFailed);

        var current = await olivia.Read(row.Batch);
        Assert.Equal(row.ETag, current.ETag);
        Assert.Equal(BatchState.Created, current.Batch.State);
    }

    [Fact]
    public async Task REQ_BT_006_Inspect_with_stale_If_Match_is_412()
    {
        var row = await As("olivia").WalkTo(BatchState.Dose2, As("ian"));

        await AssertProblem(await As("ian").Inspect(row.Batch, true, null, StaleETag), HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task REQ_BT_006_ETag_from_before_the_Worker_wrote_is_stale()
    {
        var olivia = As("olivia");
        var created = await olivia.Create();
        await olivia.Settle(created.Batch);

        await AssertProblem(await olivia.Advance(created.Batch, "Dose1", created.ETag), HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task REQ_BT_008_Advance_right_after_create_is_409_while_the_event_is_pending()
    {
        var olivia = As("olivia");
        var created = await olivia.Create();

        await AssertProblem(await olivia.Advance(created.Batch, "Dose1", created.ETag), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task REQ_BT_008_Inspect_is_409_while_the_event_is_pending()
    {
        var olivia = As("olivia");
        var row = await olivia.WalkTo(BatchState.Dose1, As("ian"));
        await olivia.Advance(row.Batch, "Dose2", row.ETag);
        var awaiting = await olivia.Read(row.Batch);

        await AssertProblem(await As("ian").Inspect(awaiting.Batch, true, null, awaiting.ETag), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task REQ_BT_008_Advance_to_Inspected_is_409_and_nothing_written()
    {
        var olivia = As("olivia");
        var row = await olivia.WalkTo(BatchState.Dose2, As("ian"));

        await AssertProblem(await olivia.Advance(row.Batch, "Inspected", row.ETag), HttpStatusCode.Conflict);

        Assert.Equal(row.ETag, (await olivia.Read(row.Batch)).ETag);
    }

    [Fact]
    public async Task REQ_BT_008_Blocked_batch_refuses_advance_and_replay_opens_it()
    {
        var olivia = As("olivia");
        var row = await Blocked();

        await AssertProblem(await olivia.Advance(row.Batch, "Dose1", row.ETag), HttpStatusCode.Conflict);

        var replay = await As("ada").Replay(row.Batch);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var batch = await ApiUser.ReadBatch(replay);
        Assert.Equal(PendingStatus.Pending, batch.Pending!.Status);
        Assert.False(batch.FailNext);
        Assert.Null(batch.Pending.Error);
    }

    [Fact]
    public async Task REQ_BT_011_Replay_is_409_unless_blocked()
    {
        var ada = As("ada");
        var waiting = await ada.Create();
        var settled = await ada.CreateSettled();

        await AssertProblem(await ada.Replay(waiting.Batch), HttpStatusCode.Conflict);
        await AssertProblem(await ada.Replay(settled.Batch), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task REQ_BT_011_Fail_next_sets_the_switch()
    {
        var created = await As("olivia").Create();

        var response = await As("ada").FailNext(created.Batch);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ApiUser.ReadBatch(response)).FailNext);
        Assert.False(string.IsNullOrEmpty(ApiUser.ETagOf(response)));
    }

    [Fact]
    public async Task REQ_BT_006_Two_concurrent_advances_exactly_one_wins()
    {
        var olivia = As("olivia");
        var row = await olivia.CreateSettled();

        var responses = await Task.WhenAll(
            olivia.Advance(row.Batch, "Dose1", row.ETag),
            olivia.Advance(row.Batch, "Dose1", row.ETag));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.PreconditionFailed);
        var current = (await olivia.Read(row.Batch)).Batch;
        Assert.Equal(BatchState.Dose1, current.State);
        Assert.Equal(2, current.Steps.Count);
    }

    [Fact]
    public async Task REQ_BT_006_Eight_concurrent_advances_write_one_step()
    {
        var olivia = As("olivia");
        var row = await olivia.CreateSettled();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => olivia.Advance(row.Batch, "Dose1", row.ETag)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(2, (await olivia.Read(row.Batch)).Batch.Steps.Count);
    }
}
