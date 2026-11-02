using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.IntegrationTests;

/// <summary>What the tests of BatchRouteTests and its neighbours share.</summary>
public abstract class BatchRouteTest(Stack stack) : StackTest(stack)
{
    protected static readonly string StaleETag = "\"00000000-0000-0000-0000-000000000000\"";

    protected static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)status, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetInt32());
    }

    protected async Task<Created> Blocked()
    {
        var created = await As("olivia").CreateSettled();
        var stored = (await Stack.Store.GetAsync(created.Batch.FarmId, created.Batch.Id, default))!;
        var dead = new PendingEvent { EventId = "x", TraceId = "t", Status = PendingStatus.Dead, Error = "refused" };
        await Stack.Store.ReplaceAsync(stored.Batch with { Pending = dead, FailNext = true }, stored.ETag, default);
        return await As("olivia").Read(created.Batch);
    }
}
