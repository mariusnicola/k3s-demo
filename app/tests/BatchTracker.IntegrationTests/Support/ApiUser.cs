using System.Net;
using System.Net.Http.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.IntegrationTests;

public sealed record Created(Batch Batch, string ETag);

public sealed class ApiUser
{
    private readonly Stack stack;
    public HttpClient Http { get; }

    public ApiUser(Stack stack, string? user)
    {
        this.stack = stack;
        Http = stack.Api.CreateClient();
        if (user is null) return;
        // The client keeps the session cookie, as a browser would, and sends the Origin a page would.
        Http.DefaultRequestHeaders.Add("Origin", Env.Origin);
        Http.PostAsJsonAsync("/api/auth/dev-login", new { user }).GetAwaiter().GetResult().EnsureSuccessStatusCode();
    }

    public Task<HttpResponseMessage> PostCreate(string farmId = "farm-a", string? product = "Vaccine") =>
        Http.PostAsJsonAsync("/api/batches", new { farmId, product });

    public async Task<Created> Create(string farmId = "farm-a", string product = "Vaccine")
    {
        var response = await PostCreate(farmId, product);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new Created(await ReadBatch(response), ETagOf(response));
    }

    public Task<HttpResponseMessage> Get(Batch batch) => Http.GetAsync($"/api/batches/{batch.FarmId}/{batch.Id}");

    public async Task<Created> Read(Batch batch)
    {
        var response = await Get(batch);
        response.EnsureSuccessStatusCode();
        return new Created(await ReadBatch(response), ETagOf(response));
    }

    public Task<HttpResponseMessage> Advance(Batch batch, object to, string? etag) =>
        Post($"/api/batches/{batch.FarmId}/{batch.Id}/advance", new { to }, etag);

    public Task<HttpResponseMessage> Inspect(Batch batch, bool? passed, string? reason, string? etag) =>
        Post($"/api/batches/{batch.FarmId}/{batch.Id}/inspect", new { passed, reason }, etag);

    public Task<HttpResponseMessage> FailNext(Batch batch) => Http.PostAsync($"/api/batches/{batch.FarmId}/{batch.Id}/fail-next", null);
    public Task<HttpResponseMessage> Replay(Batch batch) => Http.PostAsync($"/api/batches/{batch.FarmId}/{batch.Id}/replay", null);

    /// <summary>Stands in for Dispatcher delivery when a test is about the API, not the event path.</summary>
    public async Task<Created> Settle(Batch batch)
    {
        var current = (await Read(batch)).Batch;
        var message = new BatchEvent(current.Pending!.EventId, current.Id, current.FarmId, current.Pending.TraceId, current.State.ToString());
        var result = await stack.Worker.NewClient().PostEventAsync(message, default);
        Assert.Equal(BatchTracker.Application.Dispatching.WorkerResult.Done, result);
        return await Read(batch);
    }

    public async Task<Created> CreateSettled(string farmId = "farm-a") => await Settle((await Create(farmId)).Batch);

    /// <summary>Reaches the state without the event path; `inspector` signs the inspection.</summary>
    public async Task<Created> WalkTo(BatchState state, ApiUser inspector, string farmId = "farm-a")
    {
        var row = await CreateSettled(farmId);
        foreach (var next in new[] { BatchState.Dose1, BatchState.Dose2, BatchState.Inspected, BatchState.Shipped, BatchState.Closed })
        {
            if (row.Batch.State == state) return row;
            var response = next == BatchState.Inspected
                ? await inspector.Inspect(row.Batch, true, null, row.ETag)
                : await Advance(row.Batch, next.ToString(), row.ETag);
            response.EnsureSuccessStatusCode();
            row = await Settle(row.Batch);
        }
        return row;
    }

    public static async Task<Batch> ReadBatch(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Batch>(JsonDefaults.Options))!;

    public static string ETagOf(HttpResponseMessage response) => response.Headers.ETag!.ToString();

    private async Task<HttpResponseMessage> Post(string path, object body, string? etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await Http.SendAsync(request);
    }
}
