using System.Net;
using BatchTracker.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BatchTracker.UnitTests;

/// <summary>What the tests of RetryHandlerTests and its neighbours share.</summary>
public abstract class RetryHandlerTest
{
    protected sealed class Script(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public int Calls;
        public readonly List<string?> Bodies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(token));
            return await answer(++Calls, request, token);
        }
    }

    protected sealed class Capture : ILogger
    {
        public readonly List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    protected static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    protected readonly List<TimeSpan> waits = [];
    protected readonly Capture log = new();
    protected double random = 1;

    protected HttpClient Client(Script script) => new(new RetryHandler(() => random, Record, () => Now, log) { InnerHandler = script })
    {
        BaseAddress = new Uri("http://service/")
    };

    protected Task Record(TimeSpan wait, CancellationToken token)
    {
        waits.Add(wait);
        return Task.CompletedTask;
    }

    protected static Task<HttpResponseMessage> Status(HttpStatusCode code) => Task.FromResult(new HttpResponseMessage(code));

    protected static Task<HttpResponseMessage> After(string value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", value);
        return Task.FromResult(response);
    }
}
