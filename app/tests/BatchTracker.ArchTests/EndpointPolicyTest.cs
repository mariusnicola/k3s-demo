using BatchTracker.Api;
using BatchTracker.Application;
using BatchTracker.Dispatcher;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using BatchTracker.Live;
using BatchTracker.Worker;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BatchTracker.ArchTests;

/// <summary>What the tests of EndpointPolicyTests and its neighbours share.</summary>
public abstract class EndpointPolicyTest
{
    static EndpointPolicyTest()
    {
        Environment.SetEnvironmentVariable("BATCH_STORE", "cosmos");
        Environment.SetEnvironmentVariable("COSMOS_CONNECTION", "AccountEndpoint=https://127.0.0.1:1/;AccountKey=Y2hlY2s=;");
        Environment.SetEnvironmentVariable("SERVICEBUS_CONNECTION", "Endpoint=sb://127.0.0.1:1;SharedAccessKeyName=n;SharedAccessKey=k;UseDevelopmentEmulator=true;");
        using var keys = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("JWT_PRIVATE_KEY", Convert.ToBase64String(keys.ExportPkcs8PrivateKey()));
        Environment.SetEnvironmentVariable("JWT_PUBLIC_KEY", Convert.ToBase64String(keys.ExportSubjectPublicKeyInfo()));
        Environment.SetEnvironmentVariable("ALLOWED_ORIGINS", "http://arch.test");
        Environment.SetEnvironmentVariable("LINE_MACHINE_TOKEN", "arch-test-machine-token");
        Environment.SetEnvironmentVariable("MONITOR_MACHINE_TOKEN", "arch-test-monitor-token");
        Environment.SetEnvironmentVariable("INTERNAL_TOKEN", "arch-test-token");
        Environment.SetEnvironmentVariable("WORKER_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("LIVE_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("API_URL", "http://127.0.0.1:9");
    }

    protected sealed class Host<TMarker> : WebApplicationFactory<TMarker> where TMarker : class
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureTestServices(services =>
            {
                // Mapping is what is inspected here, so nothing needs to run.
                foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                    services.Remove(hosted);
            });
        }
    }

    protected static List<Route> RoutesOf<TMarker>() where TMarker : class
    {
        using var host = new Host<TMarker>();
        return host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => new Route(
                e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Single() ?? "ANY",
                "/" + e.RoutePattern.RawText!.TrimStart('/'),
                e.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList(),
                e.Metadata.GetMetadata<DisableRateLimitingAttribute>() is not null,
                e.Metadata.GetMetadata<HealthRoute>() is not null,
                e.Metadata.GetMetadata<MachineRoute>() is not null))
            .ToList();
    }

    protected static string[] RateExempt(IEnumerable<Route> routes) => Names(routes.Where(r => r.RateExempt));

    protected static string[] InHealthBucket(IEnumerable<Route> routes) => Names(routes.Where(r => r.HealthBucket));

    /// <summary>The routes of every service.</summary>
    protected static IEnumerable<Route> EveryRoute() =>
        RoutesOf<ApiMarker>().Concat(RoutesOf<WorkerMarker>()).Concat(RoutesOf<DispatcherMarker>())
            .Concat(RoutesOf<LiveMarker>()).Concat(RoutesOf<LineMarker>());

    /// <summary>A route as a request would send it.</summary>
    protected static string Filled(string path) => path.Replace("{farmId}", "farm-a").Replace("{id}", "b1");

    protected static void EveryRouteDeclaresAccess(IEnumerable<Route> routes) => Assert.All(routes, route =>
        Assert.True(route.Anonymous || route.Policies.Count > 0, $"{route.Method} {route.Path} declares no policy"));

    protected static string[] Names(IEnumerable<Route> routes) =>
        routes.Select(r => $"{r.Method} {r.Path}").Order(StringComparer.Ordinal).ToArray();

    protected static string Root
    {
        get
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder.GetFiles("BatchTracker.sln").Length == 0) folder = folder.Parent!;
            return folder.FullName;
        }
    }

    protected static IEnumerable<string> SourceFiles()
    {
        return Directory.EnumerateFiles(Path.Combine(Root, "src"), "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    protected static string Source(string file) => File.ReadAllText(SourceFiles().Single(f => f.EndsWith(file)));
}
