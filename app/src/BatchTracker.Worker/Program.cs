using BatchTracker.Application;
using BatchTracker.Infrastructure;
using BatchTracker.Worker;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.HideRequestUrls();
builder.AddCorrelation();
builder.ReportLogsToLive("worker");
builder.Services.AddRequiredSettings(ServiceSettings.Worker);
var configuration = builder.Configuration;

builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Apply(o.SerializerOptions));
builder.Services.AddInternalAuth(configuration);
builder.Services.AddGracefulShutdown();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddBatchStore(configuration);
builder.Services.AddHopReporter(configuration);
builder.Services.NamePodOnHops(configuration);
builder.Services.AddSingleton(Registration.ComplianceOptionsFrom(configuration));
builder.Services.AddBatchCounters(MetricNames.Worker, MetricNames.WorkerStages, MetricNames.WorkerWorkInFlight);
builder.Services.AddSingleton<EventRecorder>();
builder.Services.AddSingleton<ComplianceRun>();

builder.Services.AddRouteRateLimits();

var app = builder.Build();
app.UseCorrelation();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapWorker();
app.MapMetrics();
app.Run();
