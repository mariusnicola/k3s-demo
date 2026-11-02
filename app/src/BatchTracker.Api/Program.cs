using BatchTracker.Api;
using BatchTracker.Application;
using BatchTracker.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.HideRequestUrls();
builder.AddCorrelation();
builder.ReportLogsToLive("api");
builder.Services.AddRequiredSettings(ServiceSettings.Api);
var configuration = builder.Configuration;

builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Apply(o.SerializerOptions));
builder.Services.AddUserAuth(configuration);
builder.Services.AddRateLimits();
builder.Services.AddFailureDelays();
builder.Services.AddGracefulShutdown();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(s => new TokenIssuer(configuration.Require("JWT_PRIVATE_KEY"), s.GetRequiredService<TimeProvider>()));
builder.Services.AddBatchWork(configuration);

var app = builder.Build();
app.UseCorrelation();
app.UseApiStageTimes();
app.UseOriginGuard();
app.UseAuthentication();
app.UseRateLimiter();
app.UseFailureDelays();
app.UseAuthorization();
app.MapApi();
app.MapMetrics();
app.Run();
