using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Dispatcher;
using BatchTracker.Infrastructure;
using k8s;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.HideRequestUrls();
builder.AddCorrelation();
builder.ReportLogsToLive("dispatcher");
builder.Services.AddRequiredSettings(ServiceSettings.Dispatcher);
var configuration = builder.Configuration;

builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Apply(o.SerializerOptions));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddShutdownLimit();
builder.Services.AddBatchStore(configuration);
builder.Services.AddHopReporter(configuration);
builder.Services.NamePodOnHops(configuration);
builder.Services.AddWorkerClient(configuration);
builder.Services.AddEventQueue(configuration);
builder.Services.AddDispatching();
builder.Services.AddSingleton<IKubernetes>(_ => new Kubernetes(KubernetesClientConfiguration.InClusterConfig()));
builder.Services.AddHostedService<RunnerService>();
builder.Services.AddHostedService<KubernetesElection>();
builder.Services.AddHostedService<PendingCountService>();

builder.Services.AddRouteRateLimits();

var app = builder.Build();
app.UseCorrelation();
app.UseRateLimiter();
app.MapGet("/healthz", () => Results.Ok()).LimitedAsHealth();
app.MapMetrics();
app.MapGet("/status", (DispatcherStatus status) => Results.Ok(new { status.Leader, status.Connected, status.SecondsSinceSweep })).LimitedAsHealth();
app.Run();
