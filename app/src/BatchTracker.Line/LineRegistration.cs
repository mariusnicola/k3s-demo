using BatchTracker.Infrastructure;

namespace BatchTracker.Line;

/// <summary>What the Line registers around its engine.</summary>
public static class LineRegistration
{
    /// <summary>The clock, the options, the log and the API client the engine uses.</summary>
    public static void AddLineDependencies(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new LineOptions
        {
            MachineToken = configuration.Require(MachineCredential.Setting),
            OpenSocket = LineSocket.Through(configuration.Require("API_URL"))
        });
        services.AddSingleton<LineLog>();
        services.AddSingleton<LineSettled>();
        services.AddHostedService<LineSocket>();
        services.AddHttpClient<LineApi>(http =>
            http.BaseAddress = new Uri(configuration.Require("API_URL").TrimEnd('/') + "/api/")).AddRetries();
    }
}
