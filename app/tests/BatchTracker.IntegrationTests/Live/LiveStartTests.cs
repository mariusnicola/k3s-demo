using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Live;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

/// <summary>Real Live hosts over a store whose scan is scripted, so it can hang or fail on cue.</summary>
public class LiveStartTests : IAsyncLifetime
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private const string Path = "/ws/hops";

    private sealed class ScriptedStore(Func<int, CancellationToken, Task<IReadOnlyList<Versioned>>> scan) : IBatchStore
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);
        public Dictionary<string, Versioned> Rows { get; } = [];

        public async Task<LiveScan> FindForLiveAsync(CancellationToken token)
        {
            var rows = await scan(Interlocked.Increment(ref calls), token);
            var counts = rows.GroupBy(r => (r.Batch.FarmId, r.Batch.State));
            return new LiveScan([.. counts.Select(g => new StateCount(g.Key.FarmId, g.Key.State.ToString(), g.Count()))], rows);
        }

        public Task<Versioned?> GetAsync(string farmId, string id, CancellationToken token) =>
            Task.FromResult(Rows.GetValueOrDefault(id));

        public Task<Versioned?> CreateAsync(Batch batch, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<Versioned>> GetManyAsync(IReadOnlyList<BatchKey> keys, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<Versioned?>> WriteManyAsync(IReadOnlyList<BundleWrite> writes, CancellationToken token) => throw new NotSupportedException();
        public Task<Versioned?> ReplaceAsync(Batch batch, string etag, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<Versioned>> ListAsync(BatchFilter filter, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<Versioned>> FindByPendingStatusAsync(string status, int max, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<Versioned>> FindSentBeforeAsync(DateTimeOffset cutoff, int max, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<Versioned>> MarkSentAsync(IReadOnlyList<Versioned> rows, DateTimeOffset at, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<Versioned>> FindInStateAsync(BatchState state, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<Batch>> FindForReportsAsync(IReadOnlyList<string>? farms, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> ReachableAsync(CancellationToken token) => Task.FromResult(true);
    }

    private sealed class ScriptedLive(IBatchStore store, LiveScanRules? rules) : PodHost<LiveMarker>
    {
        protected override void Replace(IServiceCollection services)
        {
            services.RemoveAll<IBatchStore>().AddSingleton(store);
            if (rules is not null) services.RemoveAll<LiveScanRules>().AddSingleton(rules);
        }
    }

    private static readonly IReadOnlyList<Versioned> Nothing = [];
    private readonly List<ScriptedLive> hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in hosts) await host.DisposeAsync();
    }

    private ScriptedLive Start(IBatchStore store, LiveScanRules? rules = null)
    {
        var host = new ScriptedLive(store, rules);
        hosts.Add(host);
        return host;
    }

    private static async Task<IReadOnlyList<Versioned>> HangAsync(CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        return Nothing;
    }

    private static Versioned Row(string farm) => new(
        new Batch
        {
            Id = Guid.NewGuid().ToString(),
            FarmId = farm,
            Product = "Vaccine",
            State = BatchState.Dose1,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 1
        },
        "\"etag-1\"");

    private static Task<WebSocket> OpenAsync(ScriptedLive host, string user = "ada") =>
        Sockets.OpenAsync(host.Server, Path, Sockets.CookieFor(user));

    private static async Task<JsonElement> SnapshotAsync(ScriptedLive host)
    {
        using var socket = await OpenAsync(host);
        return await Sockets.ReceiveTypeAsync(socket, "snapshot");
    }

    /// <summary>One socket throughout, because a socket per poll runs into the connect limit.</summary>
    private static async Task WaitUntilReconciledAsync(ScriptedLive host)
    {
        using var socket = await OpenAsync(host);
        var frame = await Sockets.ReceiveTypeAsync(socket, "snapshot");
        while (frame.GetProperty("reconciledAt").ValueKind == JsonValueKind.Null)
            frame = await Sockets.ReceiveTypeAsync(socket, "summary");
    }

    [Fact]
    public async Task REQ_BT_028_A_hanging_first_scan_leaves_the_pod_serving_and_taking_hops()
    {
        var store = new ScriptedStore((_, token) => HangAsync(token));
        var host = Start(store);

        var watch = Stopwatch.StartNew();
        using var http = host.CreateClient();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Start took {watch.Elapsed}.");

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/ready")).StatusCode);

        var hop = new Hop("trace-1", "b", "farm-a", HopNames.Api, "Created", DateTimeOffset.UtcNow, true);
        Assert.Equal(HttpStatusCode.Accepted, (await http.Internal().PostAsJsonAsync("/hops", hop, JsonDefaults.Options)).StatusCode);

        var snapshot = await SnapshotAsync(host);
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("reconciledAt").ValueKind);
        Assert.Contains(snapshot.GetProperty("hops").EnumerateArray(), h => h.GetProperty("traceId").GetString() == "trace-1");
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task REQ_BT_028_A_failing_first_scan_is_tried_again_without_waiting_for_the_minute(Type failure)
    {
        var store = new ScriptedStore((call, _) => call < 3
            ? throw (Exception)Activator.CreateInstance(failure)!
            : Task.FromResult(Nothing));
        var host = Start(store);
        host.CreateClient();

        await WaitUntilReconciledAsync(host);

        Assert.Equal(3, store.Calls);
    }

    [Fact]
    public async Task REQ_BT_028_A_hung_scan_is_given_up_and_tried_again_with_the_pod_still_up()
    {
        var store = new ScriptedStore((call, token) => call == 1 ? HangAsync(token) : Task.FromResult(Nothing));
        var host = Start(store, new LiveScanRules { AttemptLimit = TimeSpan.FromMilliseconds(100) });
        using var http = host.CreateClient();

        await WaitUntilReconciledAsync(host);

        Assert.Equal(2, store.Calls);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public void REQ_BT_028_A_store_that_blocks_its_thread_does_not_hold_the_start()
    {
        using var release = new ManualResetEventSlim();
        var store = new ScriptedStore((_, _) =>
        {
            release.Wait(TimeSpan.FromSeconds(8));
            return Task.FromResult(Nothing);
        });
        var host = Start(store);

        var watch = Stopwatch.StartNew();
        host.CreateClient();
        var took = watch.Elapsed;
        release.Set();

        Assert.True(took < TimeSpan.FromSeconds(5), $"Start took {took}.");
    }

    [Fact]
    public async Task REQ_BT_028_A_stop_during_the_first_scan_ends_promptly()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedStore(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            return await HangAsync(token);
        });
        var host = Start(store);
        host.CreateClient();

        var watch = Stopwatch.StartNew();
        await host.DisposeAsync();

        Assert.True(cancelled.Task.IsCompleted, "The scan was not told to stop.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Stop took {watch.Elapsed}.");
    }

    [Fact]
    public async Task REQ_BT_028_A_stop_while_waiting_to_try_again_ends_promptly()
    {
        var store = new ScriptedStore((_, _) => throw new InvalidOperationException());
        var host = Start(store);
        host.CreateClient();

        var watch = Stopwatch.StartNew();
        while (store.Calls < 6 && watch.Elapsed < Limit) await Task.Delay(10);

        watch.Restart();
        await host.DisposeAsync();

        // The sixth wait is at least 3.2 s, so a stop that sat it out shows.
        Assert.True(store.Calls >= 6, $"Only {store.Calls} scans ran.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Stop took {watch.Elapsed}.");
    }

    [Fact]
    public async Task REQ_BT_028_A_client_connected_before_the_first_scan_gets_the_rows_when_it_finishes()
    {
        var release = new TaskCompletionSource<IReadOnlyList<Versioned>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedStore((_, token) => release.Task.WaitAsync(token));
        var host = Start(store);
        host.CreateClient();
        using var socket = await OpenAsync(host);
        var snapshot = await Sockets.ReceiveTypeAsync(socket, "snapshot");
        Assert.Equal(0, snapshot.GetProperty("summary").GetProperty("byState").GetProperty("Dose1").GetInt32());

        var row = Row("farm-a");
        store.Rows[row.Batch.Id] = row;
        release.SetResult([row]);

        var frame = await Sockets.ReceiveTypeAsync(socket, "batch");
        Assert.Equal(row.Batch.Id, frame.GetProperty("batch").GetProperty("id").GetString());
        var summary = await Sockets.ReceiveTypeAsync(socket, "summary");
        Assert.Equal(1, summary.GetProperty("summary").GetProperty("byState").GetProperty("Dose1").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, summary.GetProperty("reconciledAt").ValueKind);
    }

    [Fact]
    public async Task REQ_BT_052_Each_scan_runs_with_an_id_of_its_own()
    {
        var seen = new List<string?>();
        var store = new ScriptedStore((call, _) =>
        {
            lock (seen) seen.Add(Correlation.Current);
            return call < 3 ? throw new InvalidOperationException() : Task.FromResult(Nothing);
        });
        var host = Start(store);
        host.CreateClient();

        await WaitUntilReconciledAsync(host);

        Assert.Equal(3, seen.Count);
        Assert.All(seen, id => Assert.True(Correlation.IsValid(id)));
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public async Task REQ_BT_028_A_client_learns_the_store_was_read_even_when_the_store_is_empty()
    {
        var release = new TaskCompletionSource<IReadOnlyList<Versioned>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedStore((_, token) => release.Task.WaitAsync(token));
        var host = Start(store);
        host.CreateClient();
        using var socket = await OpenAsync(host);
        await Sockets.ReceiveTypeAsync(socket, "snapshot");

        release.SetResult([]);

        var summary = await Sockets.ReceiveTypeAsync(socket, "summary");
        Assert.NotEqual(JsonValueKind.Null, summary.GetProperty("reconciledAt").ValueKind);
    }
}
