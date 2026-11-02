using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Live;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.HideRequestUrls();
builder.AddCorrelation();
builder.AddServiceLogs("live");
builder.Services.AddRequiredSettings(ServiceSettings.Live);

builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Apply(o.SerializerOptions));
builder.Services.AddUserAuth(builder.Configuration);
builder.Services.AddInternalReports(builder.Configuration);
builder.Services.AddRateLimits();
builder.Services.AddFailureDelays();
builder.Services.AddGracefulShutdown();
builder.Services.AddSocketRules();
builder.Services.AddLiveState(builder.Configuration);
builder.Services.AddHostedService<HopTicker>();
builder.Services.AddHostedService<LiveReconciler>();

var app = builder.Build();
app.UseCorrelation();
app.UseWebSockets();
app.UseOriginGuard();
app.UseAuthentication();
app.UseRateLimiter();
app.UseFailureDelays();
app.UseAuthorization();

app.MapLive();
app.MapReady();
app.Run();
