using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BatchTracker.Infrastructure;

public static class Settings
{
    public static string Require(this IConfiguration configuration, string name) =>
        configuration[name] is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Environment variable {name} is required.");
}

public static class RequestLog
{
    /// <summary>
    /// The hosting log writes every query string, so a token someone puts in a URL would land in it. The framework's other
    /// lines per request cost more than the request on a pod held to its CPU limit, so only their problems are written.
    /// </summary>
    public static ILoggingBuilder HideRequestUrls(this ILoggingBuilder logging) => logging
        .AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning)
        .AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning)
        .AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning)
        .AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Apply(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Apply(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
