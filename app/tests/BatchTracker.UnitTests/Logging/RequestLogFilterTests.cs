using BatchTracker.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BatchTracker.UnitTests;

/// <summary>Framework lines per request cost a throttled pod more than the request, so only their problems are written.</summary>
public sealed class RequestLogFilterTests
{
    /// <summary>Takes everything, so what is left off is the filter's doing.</summary>
    private sealed class Sink : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        public void Dispose() { }
    }

    private static ILogger Logger(string category)
    {
        var services = new ServiceCollection();
        services.AddLogging(l => l.AddProvider(new Sink()).HideRequestUrls());
        return services.BuildServiceProvider().GetRequiredService<ILoggerFactory>().CreateLogger(category);
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics")]
    [InlineData("Microsoft.AspNetCore.Routing.EndpointMiddleware")]
    [InlineData("Microsoft.AspNetCore.Http.Result.OkObjectResult")]
    [InlineData("System.Net.Http.HttpClient.IHopReporter.ClientHandler")]
    [InlineData("System.Net.Http.HttpClient.IChangeReporter.LogicalHandler")]
    [InlineData("System.Net.Http.HttpClient.IWorkerClient.LogicalHandler")]
    public void REQ_BT_053_A_framework_line_per_request_is_written_only_as_a_problem(string category)
    {
        var logger = Logger(category);

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
    }

    [Theory]
    [InlineData("BatchTracker.Line.LineEngine")]
    [InlineData("BatchTracker.Infrastructure.RetryHandler")]
    [InlineData("Microsoft.Hosting.Lifetime")]
    [InlineData("Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler")]
    [InlineData("Microsoft.AspNetCore.Authorization.DefaultAuthorizationService")]
    public void REQ_BT_053_The_app_the_start_and_stop_and_the_refusal_lines_stay_at_information(string category)
    {
        Assert.True(Logger(category).IsEnabled(LogLevel.Information));
    }
}
