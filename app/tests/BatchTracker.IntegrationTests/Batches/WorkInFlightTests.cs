using System.Net.Http.Json;
using System.Text.RegularExpressions;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

/// <summary>The gauge the Monitor reads as busy, held open by a store call that waits.</summary>
public partial class WorkInFlightTests(Stack stack) : StackTest(stack)
{
    [GeneratedRegex(@"^bt_worker_work_in_flight (\d+)$", RegexOptions.Multiline)]
    private static partial Regex Sample();

    private static async Task<long> ReadAsync<T>(WebApplicationFactory<T> host) where T : class
    {
        var found = Sample().Match(await host.CreateClient().GetStringAsync("/metrics"));
        Assert.True(found.Success, "no gauge on /metrics");
        return long.Parse(found.Groups[1].Value);
    }

    [Fact]
    public async Task REQ_BT_026_A_Worker_at_rest_reads_zero_rather_than_nothing()
    {
        await using var worker = new WorkerHost();

        Assert.Equal(0, await ReadAsync(worker));
    }

    [Fact]
    public async Task REQ_BT_026_The_api_exports_no_worker_gauge()
    {
        Assert.DoesNotContain("bt_worker_work_in_flight", await Stack.Api.CreateClient().GetStringAsync("/metrics"));
    }

    [Theory]
    [InlineData("/events")]
    [InlineData("/nightly/run")]
    public async Task REQ_BT_026_Work_reads_one_while_a_Worker_route_runs_and_zero_after(string path)
    {
        var created = await As("olivia").Create();
        var store = new HoldingStore(Stack.Store);
        await using var worker = new WorkerHost(services => services.RemoveAll<IBatchStore>().AddSingleton<IBatchStore>(store));
        var client = worker.CreateClient();
        client.DefaultRequestHeaders.Add(HttpWorkerClient.TokenHeader, Env.InternalToken);
        var message = new BatchEvent(created.Batch.Pending!.EventId, created.Batch.Id, created.Batch.FarmId, created.Batch.Pending.TraceId, created.Batch.State.ToString());

        var running = path == "/events" ? client.PostAsJsonAsync(path, message, JsonDefaults.Options) : client.PostAsync(path, null);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, await ReadAsync(worker));

        store.Release.SetResult();
        await running;
        Assert.Equal(0, await ReadAsync(worker));
    }

    /// <summary>Waits inside the first read each route makes, so the test sees the work while it runs.</summary>
    private sealed class HoldingStore(IBatchStore inner) : IBatchStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task Hold()
        {
            Entered.TrySetResult();
            await Release.Task;
        }

        public async Task<Versioned?> GetAsync(string farmId, string id, CancellationToken token)
        {
            await Hold();
            return await inner.GetAsync(farmId, id, token);
        }

        public async Task<IReadOnlyList<Versioned>> FindInStateAsync(BatchState state, CancellationToken token)
        {
            await Hold();
            return await inner.FindInStateAsync(state, token);
        }

        public Task<Versioned?> ReplaceAsync(Batch batch, string etag, CancellationToken token) => inner.ReplaceAsync(batch, etag, token);
        public Task<Versioned?> CreateAsync(Batch batch, CancellationToken token) => inner.CreateAsync(batch, token);
        public Task<IReadOnlyList<Versioned>> GetManyAsync(IReadOnlyList<BatchKey> keys, CancellationToken token) => inner.GetManyAsync(keys, token);
        public Task<IReadOnlyList<Versioned?>> WriteManyAsync(IReadOnlyList<BundleWrite> writes, CancellationToken token) => inner.WriteManyAsync(writes, token);
        public Task<IReadOnlyList<Versioned>> ListAsync(BatchFilter filter, CancellationToken token) => inner.ListAsync(filter, token);
        public Task<IReadOnlyList<Versioned>> FindByPendingStatusAsync(string status, int max, CancellationToken token) =>
            inner.FindByPendingStatusAsync(status, max, token);
        public Task<IReadOnlyList<Versioned>> FindSentBeforeAsync(DateTimeOffset cutoff, int max, CancellationToken token) =>
            inner.FindSentBeforeAsync(cutoff, max, token);
        public Task<IReadOnlyList<Versioned>> MarkSentAsync(IReadOnlyList<Versioned> rows, DateTimeOffset at, CancellationToken token) =>
            inner.MarkSentAsync(rows, at, token);
        public Task<IReadOnlyList<Batch>> FindForReportsAsync(IReadOnlyList<string>? farms, CancellationToken token) =>
            inner.FindForReportsAsync(farms, token);
        public Task<LiveScan> FindForLiveAsync(CancellationToken token) => inner.FindForLiveAsync(token);
        public Task<bool> ReachableAsync(CancellationToken token) => inner.ReachableAsync(token);
    }
}
