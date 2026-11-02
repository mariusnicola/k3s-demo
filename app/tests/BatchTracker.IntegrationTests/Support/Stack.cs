using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Azure.Messaging.ServiceBus;
using BatchTracker.Api;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Dispatcher;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using BatchTracker.Live;
using BatchTracker.Worker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BatchTracker.IntegrationTests;

public static class Env
{
    public const string Database = "batch-tracker-test";
    public const string Container = "batches";
    public const string Queue = "batch-events-test";
    public const string InternalToken = "test-internal-token";
    public const string LineMachineToken = "test-line-machine-token";
    public const string MonitorMachineToken = "test-monitor-machine-token";
    public const string Origin = "http://batch-tracker.test";

    /// <summary>Fresh pair each run, so no key sits in the repository.</summary>
    private static readonly ECDsa Keys = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public static readonly string PrivateKey = Convert.ToBase64String(Keys.ExportPkcs8PrivateKey());
    public static readonly string PublicKey = Convert.ToBase64String(Keys.ExportSubjectPublicKeyInfo());

    /// <summary>The emulators' published well-known keys; neither enforces authentication.</summary>
    public const string Cosmos = "AccountEndpoint=https://127.0.0.1:8091/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;DisableServerCertificateValidation=True;";
    public const string Bus = "Endpoint=sb://127.0.0.1:5682;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    /// <summary>From the shell that runs the tests: it holds the container's password, which never sits in a file here.</summary>
    public static string Mongo => Environment.GetEnvironmentVariable("TEST_MONGO_CONNECTION") ?? "mongodb://127.0.0.1:27018";

    /// <summary>Real environment variables, so tests read configuration the way a pod does.</summary>
    [ModuleInitializer]
    public static void Apply()
    {
        Environment.SetEnvironmentVariable("BATCH_STORE", "cosmos");
        Environment.SetEnvironmentVariable("COSMOS_CONNECTION", Cosmos);
        Environment.SetEnvironmentVariable("COSMOS_DATABASE", Database);
        Environment.SetEnvironmentVariable("COSMOS_CONTAINER", Container);
        Environment.SetEnvironmentVariable("SERVICEBUS_CONNECTION", Bus);
        Environment.SetEnvironmentVariable("SERVICEBUS_QUEUE", Queue);
        Environment.SetEnvironmentVariable("JWT_PRIVATE_KEY", PrivateKey);
        Environment.SetEnvironmentVariable("JWT_PUBLIC_KEY", PublicKey);
        Environment.SetEnvironmentVariable("ALLOWED_ORIGINS", Origin);
        Environment.SetEnvironmentVariable("LINE_MACHINE_TOKEN", LineMachineToken);
        Environment.SetEnvironmentVariable("MONITOR_MACHINE_TOKEN", MonitorMachineToken);
        Environment.SetEnvironmentVariable("COOKIE_SECURE", "false");
        Environment.SetEnvironmentVariable("DEV_LOGIN_ENABLED", "true");
        Environment.SetEnvironmentVariable("INTERNAL_TOKEN", InternalToken);
        Environment.SetEnvironmentVariable("WORKER_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("LIVE_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("API_URL", "http://127.0.0.1:9");
    }
}

/// <summary>Pods run as Production, where bad input is a 400 and not a developer exception.</summary>
public abstract class PodHost<TMarker> : WebApplicationFactory<TMarker> where TMarker : class
{
    private static readonly RateRule Wide = new(1_000_000, 1_000_000);
    private static readonly FailureRules NoDelay = new() { RefuseFrom = int.MaxValue, MaxDelay = TimeSpan.Zero, FirstDelay = TimeSpan.Zero, MaxWaiting = int.MaxValue };
    private static readonly RateRules Roomy = new() { Anonymous = Wide, User = Wide, Machine = Wide, DeepReady = Wide, Health = Wide, BrowserReport = Wide };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureTestServices(services =>
        {
            // Tests share one client; the limits themselves are proved with the real rules in RateLimitTests.
            services.RemoveAll<RateRules>().AddSingleton(Roomy);
            services.RemoveAll<ShutdownRules>().AddSingleton(new ShutdownRules { Drain = TimeSpan.Zero });
            services.RemoveAll<FailureRules>().AddSingleton(NoDelay);
            Replace(services);
        });
    }

    protected virtual void Replace(IServiceCollection services)
    {
    }
}

public sealed class WorkerHost(Action<IServiceCollection>? replace = null) : PodHost<WorkerMarker>
{
    protected override void Replace(IServiceCollection services) => replace?.Invoke(services);
}

public sealed class ApiHost(WorkerHost worker) : PodHost<ApiMarker>
{
    protected override void Replace(IServiceCollection services) =>
        services.RemoveAll<IWorkerClient>().AddSingleton(_ => worker.NewClient());
}

public sealed class DispatcherHost(Action<IServiceCollection> replace) : PodHost<DispatcherMarker>
{
    public LeadershipSignal Leadership => Services.GetRequiredService<LeadershipSignal>();

    protected override void Replace(IServiceCollection services)
    {
        // The election needs a cluster; tests hold the lease through the signal it drives.
        var election = services.Single(d => d.ImplementationType == typeof(KubernetesElection));
        services.Remove(election);
        replace(services);
    }
}

public sealed class LiveHost(Action<IServiceCollection>? replace = null) : PodHost<LiveMarker>
{
    protected override void Replace(IServiceCollection services) => replace?.Invoke(services);
}

public sealed class LineHost(ApiHost api, LineOptions? options = null, Func<HttpMessageHandler, HttpMessageHandler>? wrap = null) : PodHost<LineMarker>
{
    protected override void Replace(IServiceCollection services)
    {
        var held = services.Last(d => d.ServiceType == typeof(LineOptions));
        services.RemoveAll<LineOptions>().AddSingleton(s => Quick(options is null
            ? (LineOptions)held.ImplementationFactory!(s)
            : options.MachineToken.Length > 0 ? options : options with { MachineToken = Env.LineMachineToken }));
        services.AddHttpClient<LineApi>(http => http.BaseAddress = new Uri("http://localhost/api/"))
            .ConfigurePrimaryHttpMessageHandler(() => (wrap ?? (h => h))(api.Server.CreateHandler()));
    }

    /// <summary>A walk has six steps, so a second a tick would add six seconds to every line test.</summary>
    private static LineOptions Quick(LineOptions chosen) =>
        chosen.BundleEvery == new LineOptions().BundleEvery ? chosen with { BundleEvery = TimeSpan.FromMilliseconds(10) } : chosen;
}

public static class WorkerClients
{
    /// <summary>A client as one of our own services calls another.</summary>
    public static HttpClient Internal(this HttpClient client)
    {
        client.DefaultRequestHeaders.Add(HttpWorkerClient.TokenHeader, Env.InternalToken);
        return client;
    }

    public static IWorkerClient NewClient(this WorkerHost worker) =>
        new HttpWorkerClient(worker.CreateClient(), new WorkerRoute(Env.InternalToken, null));
}

[CollectionDefinition(Name)]
public sealed class StackCollection : ICollectionFixture<Stack>
{
    public const string Name = "stack";
}

public sealed class Stack : IAsyncLifetime
{
    public WorkerHost Worker { get; } = new();
    public ApiHost Api { get; }
    public CosmosClient Cosmos { get; } = CosmosBatchStore.CreateClient(Env.Cosmos);
    public ServiceBusClient Bus { get; } = new(Env.Bus);
    public Container Batches { get; private set; } = null!;

    public Stack() => Api = new ApiHost(Worker);

    public IBatchStore Store => Api.Services.GetRequiredService<IBatchStore>();

    public async Task InitializeAsync()
    {
        Database database = await Cosmos.CreateDatabaseIfNotExistsAsync(Env.Database);
        Batches = await database.CreateContainerIfNotExistsAsync(Env.Container, "/farmId");
    }

    public async Task ResetAsync()
    {
        using var rows = Batches.GetItemQueryIterator<Row>("SELECT c.id, c.farmId FROM c");
        while (rows.HasMoreResults)
        {
            foreach (var row in await rows.ReadNextAsync())
                await Batches.DeleteItemAsync<object>(row.Id, new PartitionKey(row.FarmId));
        }
        await DrainAsync(new ServiceBusReceiverOptions());
        await DrainAsync(new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
    }

    public async Task<int> DeadLetterCountAsync()
    {
        await using var receiver = Bus.CreateReceiver(
            Env.Queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        return (await receiver.PeekMessagesAsync(100)).Count;
    }

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        await Worker.DisposeAsync();
        await Bus.DisposeAsync();
        Cosmos.Dispose();
    }

    private async Task DrainAsync(ServiceBusReceiverOptions options)
    {
        options.ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete;
        await using var receiver = Bus.CreateReceiver(Env.Queue, options);
        while ((await receiver.ReceiveMessagesAsync(100, TimeSpan.FromMilliseconds(300))).Count > 0)
        {
        }
    }

    private sealed record Row(string Id, string FarmId);
}

[Collection(StackCollection.Name)]
public abstract class StackTest(Stack stack) : IAsyncLifetime
{
    protected Stack Stack { get; } = stack;

    public virtual Task InitializeAsync() => Stack.ResetAsync();
    public virtual Task DisposeAsync() => Task.CompletedTask;

    protected ApiUser As(string user) => new(Stack, user);
    protected ApiUser Anonymous() => new(Stack, null);
}
