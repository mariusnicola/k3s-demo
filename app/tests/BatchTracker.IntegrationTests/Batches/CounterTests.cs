using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using BatchTracker.Application;
using BatchTracker.Domain;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BatchTracker.IntegrationTests;

public partial class CounterTests(Stack stack) : StackTest(stack)
{
    [GeneratedRegex(@"^(bt_\w+)\{farm=""([\w-]+)""\} (\d+)$", RegexOptions.Multiline)]
    private static partial Regex Sample();

    private static async Task<Dictionary<(string, string), long>> ScrapeAsync<T>(WebApplicationFactory<T> host) where T : class
    {
        var text = await host.CreateClient().GetStringAsync("/metrics");
        return Sample().Matches(text).ToDictionary(
            m => (m.Groups[1].Value, m.Groups[2].Value),
            m => long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    private static long Added(
        Dictionary<(string, string), long> before, Dictionary<(string, string), long> after, string name, string farm = "farm-a") =>
        after[(name, farm)] - before[(name, farm)];

    [Fact]
    public async Task REQ_BT_046_Api_exports_its_counters_as_prometheus_text_for_every_farm()
    {
        var reply = await Stack.Api.CreateClient().GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal("text/plain", reply.Content.Headers.ContentType?.MediaType);
        var samples = await ScrapeAsync(Stack.Api);
        Assert.All(MetricNames.Api, name =>
            Assert.All(DemoUsers.KnownFarms, farm => Assert.True(samples.ContainsKey((name, farm)), $"{name} {farm}")));
    }

    [Fact]
    public async Task REQ_BT_046_Worker_exports_its_counter()
    {
        var samples = await ScrapeAsync(Stack.Worker);

        Assert.True(samples.ContainsKey((MetricNames.EventsPassed, "farm-a")));
    }

    [Fact]
    public async Task REQ_BT_046_Dispatcher_exports_its_counter()
    {
        await using var dispatcher = new DispatcherHost(_ => { });

        var samples = await ScrapeAsync(dispatcher);

        Assert.True(samples.ContainsKey((MetricNames.EventsDeadLettered, "farm-a")));
    }

    [Fact]
    public async Task REQ_BT_046_A_request_through_the_ingress_gets_nothing()
    {
        var client = Stack.Worker.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5");

        var reply = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_046_Counters_match_the_store_after_real_walks()
    {
        var api = await ScrapeAsync(Stack.Api);
        var worker = await ScrapeAsync(Stack.Worker);
        var olivia = As("olivia");
        var ian = As("ian");

        await olivia.WalkTo(BatchState.Closed, ian);
        await olivia.WalkTo(BatchState.Closed, ian);
        var rejected = await olivia.WalkTo(BatchState.Dose2, ian);
        await ian.Inspect(rejected.Batch, false, "bad", rejected.ETag);
        await olivia.Settle(rejected.Batch);

        var apiAfter = await ScrapeAsync(Stack.Api);
        var workerAfter = await ScrapeAsync(Stack.Worker);
        var rows = await Stack.Store.FindForReportsAsync(null, default);
        var steps = rows.SelectMany(b => b.Steps).ToList();
        long Reached(BatchState state) => steps.Count(s => s.State == state);

        Assert.Equal(rows.Count, Added(api, apiAfter, MetricNames.BatchesCreated));
        Assert.Equal(steps.Count, Added(api, apiAfter, MetricNames.EventsCreated));
        Assert.Equal(steps.Count, Added(worker, workerAfter, MetricNames.EventsPassed));
        Assert.Equal(Reached(BatchState.Dose1), Added(api, apiAfter, MetricNames.Dose1));
        Assert.Equal(Reached(BatchState.Dose2), Added(api, apiAfter, MetricNames.Dose2));
        Assert.Equal(Reached(BatchState.Inspected), Added(api, apiAfter, MetricNames.InspectionsPassed));
        Assert.Equal(Reached(BatchState.Rejected), Added(api, apiAfter, MetricNames.InspectionsFailed));
        Assert.Equal(Reached(BatchState.Rejected), Added(api, apiAfter, MetricNames.BatchesRejected));
        Assert.Equal(Reached(BatchState.Shipped), Added(api, apiAfter, MetricNames.BatchesShipped));
        Assert.Equal(Reached(BatchState.Closed), Added(api, apiAfter, MetricNames.BatchesClosed));
    }

    [Fact]
    public async Task REQ_BT_046_Redelivered_event_over_http_adds_nothing()
    {
        var created = await As("olivia").Create();
        var message = new BatchEvent(created.Batch.Pending!.EventId, created.Batch.Id, created.Batch.FarmId, "t", "Created");
        var client = Stack.Worker.NewClient();
        await client.PostEventAsync(message, default);
        var before = await ScrapeAsync(Stack.Worker);

        await client.PostEventAsync(message, default);

        Assert.Equal(0, Added(before, await ScrapeAsync(Stack.Worker), MetricNames.EventsPassed));
    }
}
