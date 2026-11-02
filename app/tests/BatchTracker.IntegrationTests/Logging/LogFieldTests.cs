using System.Net;
using System.Reflection;
using System.Net.Http.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Live;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

/// <summary>A write that fails in a real request reaches the log with its batch, and the log filters find it.</summary>
public class LogFieldTests(Stack stack) : StackTest(stack)
{
    /// <summary>Keeps what the API would report to Live.</summary>
    private sealed class Sink : ILogSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<ServiceLogEntry> Entries { get; } = new();
        public void Add(ServiceLogEntry entry) => Entries.Enqueue(entry);
    }

    /// <summary>The real store with writes that fail, to reach the unhandled exception path mid change.</summary>
    public class NoWrites : DispatchProxy
    {
        private IBatchStore real = null!;

        public static IBatchStore Wrap(IBatchStore real)
        {
            var store = Create<IBatchStore, NoWrites>();
            ((NoWrites)(object)store).real = real;
            return store;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(IBatchStore.ReplaceAsync) ? throw new InvalidOperationException("store away") : method.Invoke(real, args);
    }

    [Fact]
    public async Task REQ_BT_045_A_failed_change_is_found_by_the_farm_and_batch_filters()
    {
        var ada = As("ada");
        var batch = (await ada.WalkTo(BatchState.Dose1, ada)).Batch;
        var row = await ada.Read(batch);
        var sink = new Sink();
        await using var host = Stack.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ILogSink>().AddSingleton<ILogSink>(sink);
            s.RemoveAll<IBatchStore>().AddSingleton(NoWrites.Wrap(Stack.Store));
        }));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", Env.Origin);
        (await client.PostAsJsonAsync("/api/auth/dev-login", new { user = "ada" })).EnsureSuccessStatusCode();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/batches/{batch.FarmId}/{batch.Id}/advance")
        {
            Content = JsonContent.Create(new { to = "Dose2" })
        };
        request.Headers.TryAddWithoutValidation("If-Match", row.ETag);
        var reply = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, reply.StatusCode);
        var log = new ServiceLog();
        foreach (var entry in sink.Entries) log.Add(entry);
        var found = Assert.Single(log.Query(new ServiceLogQuery(Farm: batch.FarmId, Batch: batch.Id)).Entries);
        Assert.Equal("Dose2", found.Step);
    }
}
