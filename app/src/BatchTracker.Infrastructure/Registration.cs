using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace BatchTracker.Infrastructure;

public static class Registration
{
    /// <summary>Live's report routes take our token, so every reporter is built here and none can forget it.</summary>
    private static void ToLive(this HttpClient http, IConfiguration configuration)
    {
        http.BaseAddress = new Uri((configuration["LIVE_URL"] ?? "http://live:8080").TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Add(HttpWorkerClient.TokenHeader, configuration.Require("INTERNAL_TOKEN"));
    }

    /// <summary>For Live: signed-in users stay the default, and only routes naming the internal policy take the token.</summary>
    public static IServiceCollection AddInternalReports(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication().AddScheme<InternalTokenOptions, InternalTokenHandler>(
            AuthSetup.InternalPolicy, o => o.Token = configuration.Require("INTERNAL_TOKEN"));
        services.AddAuthorization(options => options.AddPolicy(
            AuthSetup.InternalPolicy, p => p.AddAuthenticationSchemes(AuthSetup.InternalPolicy).RequireAuthenticatedUser()));
        return services;
    }

    /// <summary>`BATCH_STORE` picks the class, with no default. Only the chosen store's settings are required.</summary>
    public static IServiceCollection AddBatchStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLiveReports(configuration);
        services.AddSingleton<IChangeReporter>(s => s.GetRequiredService<ReportBuffer>());
        var kind = configuration.Require("BATCH_STORE");
        Func<IServiceProvider, IBatchStore> inner = kind switch
        {
            "mongo" => services.AddMongoStore(configuration),
            "cosmos" => services.AddCosmosStore(configuration),
            _ => throw new InvalidOperationException("BATCH_STORE must be mongo or cosmos.")
        };
        services.AddSingleton(s => (IStoreReadings)inner(s));
        services.AddSingleton(s => new StoreClock(s.GetRequiredService<IStoreReadings>(), TimeProvider.System));
        services.AddSingleton<IBatchStore>(s => new ReportingBatchStore(
            inner(s), s.GetRequiredService<IChangeReporter>(), s.GetRequiredService<StoreClock>(), s.GetService<IBatchCounters>()));
        return services;
    }

    private static Func<IServiceProvider, IBatchStore> AddCosmosStore(this IServiceCollection services, IConfiguration configuration)
    {
        var database = configuration["COSMOS_DATABASE"] ?? "batch-tracker";
        var container = configuration["COSMOS_CONTAINER"] ?? "batches";
        services.AddSingleton<CosmosClient>(_ => CosmosBatchStore.CreateClient(configuration.Require("COSMOS_CONNECTION")));
        services.AddSingleton(s => new CosmosBatchStore(s.GetRequiredService<CosmosClient>(), database, container));
        return s => s.GetRequiredService<CosmosBatchStore>();
    }

    private static Func<IServiceProvider, IBatchStore> AddMongoStore(this IServiceCollection services, IConfiguration configuration)
    {
        var database = configuration["MONGO_DATABASE"] ?? "batch-tracker";
        var collection = configuration["MONGO_COLLECTION"] ?? "batches";
        services.AddSingleton<IMongoClient>(_ => MongoBatchStore.CreateClient(configuration.Require("MONGO_CONNECTION")));
        services.AddSingleton(s => new MongoBatchStore(s.GetRequiredService<IMongoClient>(), database, collection, TimeProvider.System));
        return s => s.GetRequiredService<MongoBatchStore>();
    }

    public static IServiceCollection AddHopReporter(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLiveReports(configuration);
        services.AddSingleton<IHopReporter>(s => s.GetRequiredService<ReportBuffer>());
        return services;
    }

    /// <summary>One buffer and one flush a pod, however many reporters it registers.</summary>
    private static void AddLiveReports(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(ReportBuffer))) return;
        services.AddSingleton(s => new ReportBuffer(source: s.GetService<HopSource>()));
        services.AddHttpClient(nameof(ReportFlusher), http => http.ToLive(configuration)).AddRetries();
        services.AddSingleton(s => new ReportFlusher(
            () => s.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ReportFlusher)), s.GetRequiredService<ReportBuffer>()));
        services.AddHostedService(s => s.GetRequiredService<ReportFlusher>());
    }

    /// <summary>The name of this pod, or of the machine when it runs outside a cluster.</summary>
    public static string PodName(this IConfiguration configuration) => configuration["POD_NAME"] ?? Environment.MachineName;

    /// <summary>For services that show one element per pod: every hop they report carries the pod name.</summary>
    public static IServiceCollection NamePodOnHops(this IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton(_ => new HopSource(configuration.PodName()));

    /// <summary>For a service that reports to Live. Live itself registers its own sink instead.</summary>
    public static WebApplicationBuilder ReportLogsToLive(this WebApplicationBuilder builder, string service)
    {
        builder.Services.AddLiveReports(builder.Configuration);
        builder.Services.AddSingleton<ILogSink>(s => s.GetRequiredService<ReportBuffer>());
        return builder.AddServiceLogs(service);
    }

    /// <summary>The sink is found on first use, because creating it needs the logging this provider is part of.</summary>
    public static WebApplicationBuilder AddServiceLogs(this WebApplicationBuilder builder, string service)
    {
        var configuration = builder.Configuration;
        builder.Services.AddSingleton<ILoggerProvider>(s =>
            new ServiceLogProvider(s.GetRequiredService<ILogSink>, service, configuration.PodName()));
        return builder;
    }

    public static IServiceCollection AddWorkerClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => new WorkerRoute(configuration.Require("INTERNAL_TOKEN"), configuration["WORKER_HOST"]));
        services.AddHttpClient<IWorkerClient, HttpWorkerClient>(http =>
            http.BaseAddress = new Uri(configuration.Require("WORKER_URL").TrimEnd('/') + "/")).AddRetries();
        return services;
    }

    public static IServiceCollection AddBusProbe(this IServiceCollection services, IConfiguration configuration)
    {
        return services.AddSingleton<IBusProbe>(_ => new TcpBusProbe(configuration.Require("SERVICEBUS_CONNECTION")));
    }

    public static IServiceCollection AddEventQueue(this IServiceCollection services, IConfiguration configuration)
    {
        var queue = configuration["SERVICEBUS_QUEUE"] ?? "batch-events";
        return services.AddSingleton<IEventQueueFactory>(_ => new ServiceBusQueueFactory(configuration.Require("SERVICEBUS_CONNECTION"), queue));
    }

    public static ComplianceOptions ComplianceOptionsFrom(IConfiguration configuration) =>
        new(int.TryParse(configuration["Dose2WindowDays"], out var days) ? days : Dose2Rule.DefaultWindowDays);
}
