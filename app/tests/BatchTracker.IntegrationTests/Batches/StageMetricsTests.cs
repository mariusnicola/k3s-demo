using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using BatchTracker.Application;
using BatchTracker.Domain;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BatchTracker.IntegrationTests;

/// <summary>Every stage through the real hosts and the real scrape, because Prometheus reads this text and nothing else.</summary>
public partial class StageMetricsTests(Stack stack) : StackTest(stack)
{
    [GeneratedRegex(@"^bt_stage_seconds_count\{stage=""(\w+)""\} (\d+)$", RegexOptions.Multiline)]
    private static partial Regex StageCount();

    private static async Task<Dictionary<string, long>> CountsAsync<T>(WebApplicationFactory<T> host) where T : class
    {
        var text = await host.CreateClient().GetStringAsync("/metrics");
        return StageCount().Matches(text).ToDictionary(m => m.Groups[1].Value, m => long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task REQ_BT_059_Each_service_exports_exactly_its_stages_from_the_start()
    {
        Assert.Equal(MetricNames.ApiStages, (await CountsAsync(Stack.Api)).Keys);
        Assert.Equal(MetricNames.WorkerStages, (await CountsAsync(Stack.Worker)).Keys);
    }

    [Fact]
    public async Task REQ_BT_059_A_refused_api_call_is_timed()
    {
        var before = await CountsAsync(Stack.Api);

        var reply = await Stack.Api.CreateClient().GetAsync("/api/batches");

        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
        Assert.True((await CountsAsync(Stack.Api))[Stages.Api] > before[Stages.Api]);
    }

    [Theory]
    [InlineData("/metrics")]
    [InlineData("/healthz")]
    [InlineData("/api/healthz")]
    [InlineData("/api/ready")]
    public async Task REQ_BT_059_Probes_and_scrapes_are_not_api_calls(string path)
    {
        var client = Stack.Api.CreateClient();
        var before = await CountsAsync(Stack.Api);

        await client.GetAsync(path);

        Assert.Equal(before[Stages.Api], (await CountsAsync(Stack.Api))[Stages.Api]);
    }

    [Fact]
    public async Task REQ_BT_059_A_real_step_fills_every_stage_and_stamps_the_store_clock()
    {
        var api = await CountsAsync(Stack.Api);
        var worker = await CountsAsync(Stack.Worker);
        var olivia = As("olivia");

        var created = await olivia.Create();
        await olivia.Settle(created.Batch);

        Assert.NotNull(created.Batch.Pending!.StoredAt);
        var apiAfter = await CountsAsync(Stack.Api);
        var workerAfter = await CountsAsync(Stack.Worker);
        Assert.True(apiAfter[Stages.Api] > api[Stages.Api]);
        Assert.True(apiAfter[Stages.Store] > api[Stages.Store]);
        Assert.True(workerAfter[Stages.Queue] > worker[Stages.Queue]);
        Assert.True(workerAfter[Stages.Worker] > worker[Stages.Worker]);
        Assert.True(workerAfter[Stages.Store] > worker[Stages.Store]);
    }
}
