using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Line;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.HideRequestUrls();
builder.AddCorrelation();
builder.Services.AddRequiredSettings(ServiceSettings.Line);
var configuration = builder.Configuration;

builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Apply(o.SerializerOptions));
builder.Services.AddUserAuth(configuration);
builder.Services.AddRateLimits();
builder.Services.AddFailureDelays();
builder.Services.AddGracefulShutdown();
builder.Services.AddSocketRules();
builder.Services.AddLineDependencies(configuration);
builder.Services.AddSingleton<LineEngine>();
builder.Services.AddHostedService(s => s.GetRequiredService<LineEngine>());
builder.Services.AddSingleton<LineFeed>();
builder.Services.AddHostedService(s => s.GetRequiredService<LineFeed>());

var app = builder.Build();
app.UseCorrelation();
app.UseWebSockets();
app.UseOriginGuard();
app.UseAuthentication();
app.UseRateLimiter();
app.UseFailureDelays();
app.UseAuthorization();

app.MapLine();
app.MapReady();
app.Run();
