using System.Diagnostics;
using BatchTracker.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Primitives;

namespace BatchTracker.Infrastructure;

/// <summary>First in the pipeline, so even a refusal from the outermost guard carries the id.</summary>
public sealed class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // A problem writer refuses a client that does not ask for JSON, which would cost it the id and the plain words.
        context.Request.Headers.Accept = "*/*";
        var id = IdFrom(context.Request.Headers[Correlation.Header]);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[Correlation.Header] = id;
            return Task.CompletedTask;
        });
        using var action = Correlation.Use(id);
        WriteRequestLine(context);
        await next(context);
    }

    /// <summary>
    /// The one line per request, inside the id's scope. It names the route pattern, so a token in a URL cannot reach it.
    /// Health routes stay out: probes and scrapes would outnumber the work. So do routes marked to.
    /// </summary>
    private void WriteRequestLine(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<HealthRoute>() is not null || endpoint?.Metadata.GetMetadata<NoRequestLine>() is not null) return;
        logger.LogInformation("Request {Method} {Route}", context.Request.Method, (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? "no route");
    }

    /// <summary>Two headers join into a count above one, so they are refused along with anything malformed.</summary>
    private static string IdFrom(StringValues sent) =>
        sent.Count == 1 && Correlation.IsValid(sent[0]) ? sent[0]! : Correlation.New();
}

/// <summary>A route called so often that a line per call would bury the work. Rate limits read other marks, so this one leaves them alone.</summary>
public sealed class NoRequestLine;

/// <summary>Puts the current id on every call to another service.</summary>
public sealed class CorrelationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (Correlation.Current is { } id && !request.Headers.Contains(Correlation.Header)) request.Headers.Add(Correlation.Header, id);
        return base.SendAsync(request, token);
    }
}

public static class CorrelationSetup
{
    /// <summary>Keeps a route out of the request line.</summary>
    public static TBuilder WithoutRequestLine<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new NoRequestLine());

    public static WebApplicationBuilder AddCorrelation(this WebApplicationBuilder builder)
    {
        builder.Logging.Configure(o => o.ActivityTrackingOptions = ActivityTrackingOptions.Baggage);
        builder.Logging.AddConsole(o => o.FormatterName = RedactedConsoleFormatter.FormatterName);
        builder.Logging.AddConsoleFormatter<RedactedConsoleFormatter, ConsoleFormatterOptions>();
        builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = c =>
        {
            c.ProblemDetails.Extensions[Correlation.Key] = Correlation.Current;
            ProblemWords.Apply(c.ProblemDetails, c.HttpContext);
        });
        return builder;
    }

    /// <summary>The id comes first, then the two steps that turn an exception or an empty status into a problem body.</summary>
    public static IApplicationBuilder UseCorrelation(this IApplicationBuilder app)
    {
        app.UseMiddleware<CorrelationMiddleware>();
        app.UseExceptionHandler();
        return app.UseStatusCodePages();
    }
}
