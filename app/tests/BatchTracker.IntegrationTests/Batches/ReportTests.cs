using System.Net;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;

namespace BatchTracker.IntegrationTests;

public class ReportTests(Stack stack) : StackTest(stack)
{
    private async Task<Batch> PutOverdueDose1(string farmId = "farm-a")
    {
        var ada = As("ada");
        var row = await ada.WalkTo(BatchState.Dose1, ada, farmId);
        var stored = (await Stack.Store.GetAsync(row.Batch.FarmId, row.Batch.Id, default))!;
        var old = DateTimeOffset.UtcNow.AddDays(-22);
        var steps = stored.Batch.Steps.Select(s => s.State == BatchState.Dose1 ? s with { At = old } : s).ToList();
        return (await Stack.Store.ReplaceAsync(stored.Batch with { Steps = steps }, stored.ETag, default))!.Batch;
    }

    private async Task<int> RunComplianceAsync()
    {
        var response = await As("ada").Http.PostAsync("/api/compliance/run", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("flagged").GetInt32();
    }

    [Fact]
    public async Task REQ_BT_013_Compliance_run_flags_overdue_batches_through_the_worker()
    {
        var batch = await PutOverdueDose1();

        Assert.Equal(1, await RunComplianceAsync());

        var flags = (await As("ada").Read(batch)).Batch.Flags;
        Assert.Equal(Dose2Rule.FlagType, Assert.Single(flags).Type);
    }

    [Fact]
    public async Task REQ_BT_013_Repeat_run_adds_no_flag()
    {
        var batch = await PutOverdueDose1();
        await RunComplianceAsync();

        Assert.Equal(0, await RunComplianceAsync());

        Assert.Single((await As("ada").Read(batch)).Batch.Flags);
    }

    [Fact]
    public async Task REQ_BT_013_Batch_inside_the_window_is_not_flagged()
    {
        var ada = As("ada");
        await ada.WalkTo(BatchState.Dose1, ada);

        Assert.Equal(0, await RunComplianceAsync());
    }

    [Theory]
    [InlineData(BatchState.Closed)]
    [InlineData(BatchState.Rejected)]
    public async Task B_026_A_finished_batch_keeps_its_flag_and_leaves_the_reports(BatchState finished)
    {
        var batch = await PutOverdueDose1();
        await RunComplianceAsync();
        var stored = (await Stack.Store.GetAsync(batch.FarmId, batch.Id, default))!;
        await Stack.Store.ReplaceAsync(stored.Batch with { State = finished }, stored.ETag, default);
        var ada = As("ada");

        Assert.Equal(Dose2Rule.FlagType, Assert.Single((await ada.Read(batch)).Batch.Flags).Type);

        var summary = JsonDocument.Parse(await ada.Http.GetStringAsync("/api/summary")).RootElement;
        Assert.Equal(0, summary.GetProperty("flagged").GetInt32());
        var alerts = JsonDocument.Parse(await ada.Http.GetStringAsync("/api/alerts")).RootElement;
        Assert.Equal(0, alerts.GetArrayLength());
    }

    [Fact]
    public async Task REQ_BT_013_Summary_counts_states_pending_blocked_and_flagged()
    {
        var ada = As("ada");
        await ada.Create();
        await ada.CreateSettled();
        await PutOverdueDose1();
        await RunComplianceAsync();
        var blocked = await ada.CreateSettled();
        var stored = (await Stack.Store.GetAsync(blocked.Batch.FarmId, blocked.Batch.Id, default))!;
        await Stack.Store.ReplaceAsync(
            stored.Batch with { Pending = new PendingEvent { EventId = "x", TraceId = "t", Status = PendingStatus.Dead } },
            stored.ETag, default);

        var summary = JsonDocument.Parse(await ada.Http.GetStringAsync("/api/summary")).RootElement;

        Assert.Equal(3, summary.GetProperty("byState").GetProperty("Created").GetInt32());
        Assert.Equal(1, summary.GetProperty("byState").GetProperty("Dose1").GetInt32());
        Assert.Equal(0, summary.GetProperty("byState").GetProperty("Closed").GetInt32());
        Assert.Equal(1, summary.GetProperty("pending").GetInt32());
        Assert.Equal(1, summary.GetProperty("blocked").GetInt32());
        Assert.Equal(1, summary.GetProperty("flagged").GetInt32());
    }

    [Fact]
    public async Task REQ_BT_013_Alerts_list_blocked_and_flagged_batches_of_own_farms()
    {
        var mine = await PutOverdueDose1("farm-a");
        var theirs = await PutOverdueDose1("farm-b");
        await RunComplianceAsync();

        var viewer = JsonDocument.Parse(await As("viewer").Http.GetStringAsync("/api/alerts")).RootElement;
        var ada = JsonDocument.Parse(await As("ada").Http.GetStringAsync("/api/alerts")).RootElement;

        var alert = Assert.Single(viewer.EnumerateArray());
        Assert.Equal(mine.Id, alert.GetProperty("batchId").GetString());
        Assert.Equal(Dose2Rule.FlagType, alert.GetProperty("type").GetString());
        Assert.Contains(ada.EnumerateArray(), a => a.GetProperty("batchId").GetString() == theirs.Id);
    }

    [Fact]
    public async Task REQ_BT_013_Reports_on_no_data_are_empty()
    {
        var summary = JsonDocument.Parse(await As("ada").Http.GetStringAsync("/api/summary")).RootElement;
        var alerts = JsonDocument.Parse(await As("ada").Http.GetStringAsync("/api/alerts")).RootElement;

        Assert.Equal(0, summary.GetProperty("pending").GetInt32());
        Assert.Equal(0, alerts.GetArrayLength());
    }
}
