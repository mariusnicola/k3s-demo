using System.Data.Common;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BatchTracker.Infrastructure;

public enum SettingKind { Text, Url, Origins, PrivateKey, PublicKey, BatchStore, CosmosConnection, MongoConnection, BusConnection }

/// <summary>An optional setting is checked only when it is set.</summary>
public sealed record Setting(string Name, SettingKind Kind, bool Required = true);

/// <summary>The one list of what each service needs, so a missing setting stops the start, not the first request.</summary>
public static class ServiceSettings
{
    private static readonly Setting Store = new("BATCH_STORE", SettingKind.BatchStore);
    public static readonly Setting CosmosConnection = new("COSMOS_CONNECTION", SettingKind.CosmosConnection);
    public static readonly Setting MongoConnection = new("MONGO_CONNECTION", SettingKind.MongoConnection);
    private static readonly Setting Bus = new("SERVICEBUS_CONNECTION", SettingKind.BusConnection);
    private static readonly Setting InternalToken = new("INTERNAL_TOKEN", SettingKind.Text);
    private static readonly Setting WorkerUrl = new("WORKER_URL", SettingKind.Url);
    private static readonly Setting LiveUrl = new("LIVE_URL", SettingKind.Url, Required: false);
    private static readonly Setting PublicKey = new("JWT_PUBLIC_KEY", SettingKind.PublicKey);
    private static readonly Setting Origins = new("ALLOWED_ORIGINS", SettingKind.Origins);
    private static readonly Setting MachineToken = new(MachineCredential.Setting, SettingKind.Text);
    private static readonly Setting MonitorToken = new(MachineCredential.MonitorSetting, SettingKind.Text);

    public static readonly IReadOnlyList<Setting> Api =
        [new("JWT_PRIVATE_KEY", SettingKind.PrivateKey), PublicKey, Origins, MachineToken, MonitorToken, InternalToken, Store, Bus, WorkerUrl, LiveUrl];

    public static readonly IReadOnlyList<Setting> Worker = [InternalToken, Store, LiveUrl];

    public static readonly IReadOnlyList<Setting> Dispatcher = [InternalToken, Store, Bus, WorkerUrl, LiveUrl];

    public static readonly IReadOnlyList<Setting> Live = [PublicKey, Origins, InternalToken, Store, LiveUrl];

    public static readonly IReadOnlyList<Setting> Line = [PublicKey, Origins, MachineToken, new("API_URL", SettingKind.Url)];
}

public sealed class StartupSettings;

public sealed class StartupSettingsCheck(IConfiguration configuration, IReadOnlyList<Setting> settings) : IValidateOptions<StartupSettings>
{
    /// <summary>Lists every problem at once. A message names the setting and never holds its value.</summary>
    public ValidateOptionsResult Validate(string? name, StartupSettings options)
    {
        var problems = settings.Select(Problem).OfType<string>().ToList();
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    private string? Problem(Setting setting)
    {
        var value = configuration[setting.Name];
        if (string.IsNullOrWhiteSpace(value))
            return setting.Required ? $"Environment variable {setting.Name} is required." : null;
        if (setting.Kind == SettingKind.BatchStore) return StoreProblem(value);
        return IsValid(setting.Kind, value) ? null : $"Environment variable {setting.Name} is malformed.";
    }

    /// <summary>Only the chosen store's connection is needed, and it stops the start the same way any other setting does.</summary>
    private string? StoreProblem(string choice) => choice switch
    {
        "mongo" => Problem(ServiceSettings.MongoConnection),
        "cosmos" => Problem(ServiceSettings.CosmosConnection),
        _ => "Environment variable BATCH_STORE is malformed."
    };

    private static bool IsValid(SettingKind kind, string value)
    {
        try
        {
            return kind switch
            {
                SettingKind.Url => IsHttpUrl(value),
                SettingKind.Origins => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(IsOrigin),
                SettingKind.PrivateKey => TokenKeys.Private(value) is not null,
                SettingKind.PublicKey => TokenKeys.Public(value) is not null,
                SettingKind.CosmosConnection => IsCosmos(value),
                SettingKind.MongoConnection => value.StartsWith("mongodb://", StringComparison.Ordinal),
                SettingKind.BusConnection => ServiceBusConnectionStringProperties.Parse(value).Endpoint is not null,
                _ => true
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https";

    private static bool IsOrigin(string value) =>
        IsHttpUrl(value) && new Uri(value).AbsolutePath == "/" && new Uri(value).Query.Length == 0;

    private static bool IsCosmos(string value)
    {
        var parts = new DbConnectionStringBuilder { ConnectionString = value };
        return parts.ContainsKey("AccountEndpoint") && parts.ContainsKey("AccountKey");
    }
}

/// <summary>
/// Registered first. The host builds its hosted services before ValidateOnStart runs, so without this a bad
/// value fails inside a constructor with an error that does not name the setting.
/// </summary>
public sealed class StartupSettingsGate : IHostedService
{
    public StartupSettingsGate(IOptions<StartupSettings> options) => _ = options.Value;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class StartupSettingsSetup

{
    public static IServiceCollection AddRequiredSettings(this IServiceCollection services, IReadOnlyList<Setting> settings)
    {
        services.AddOptions<StartupSettings>().ValidateOnStart();
        services.AddHostedService<StartupSettingsGate>();
        return services.AddSingleton<IValidateOptions<StartupSettings>>(
            s => new StartupSettingsCheck(s.GetRequiredService<IConfiguration>(), settings));
    }
}
