using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BatchTracker.IntegrationTests;

/// <summary>What the tests of LineSocketTests and its neighbours share.</summary>
public abstract class LineSocketTest(Stack stack) : StackTest(stack)
{
    protected const string Path = "/line/ws";
    protected static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    protected readonly List<IAsyncDisposable> hosts = [];

    public override async Task DisposeAsync()
    {
        foreach (var host in hosts) await host.DisposeAsync();
    }

    protected LineHost NewLine(LineOptions? options = null, Func<HttpMessageHandler, HttpMessageHandler>? wrap = null)
    {
        var line = new LineHost(Stack.Api, options, wrap);
        hosts.Add(line);
        line.CreateClient();
        return line;
    }

    protected static Task<WebSocket> OpenAsync(LineHost line, string user = "ada") =>
        Sockets.OpenAsync(line.Server, Path, Sockets.CookieFor(user));

    protected static async Task<JsonElement> UntilLineFrameAsync(WebSocket socket, Func<JsonElement, bool> wanted)
    {
        using var timer = new CancellationTokenSource(Limit);
        while (true)
        {
            timer.Token.ThrowIfCancellationRequested();
            var frame = await Sockets.ReceiveTypeAsync(socket, "line");
            if (wanted(frame)) return frame;
        }
    }

    protected static Task RunOneAsync(LineHost line, long number = 1) =>
        line.Services.GetRequiredService<LineEngine>().RunBatchAsync(number, CancellationToken.None);

    protected sealed class ExpiresIn(TimeSpan left) : TimeProvider
    {
        private readonly DateTimeOffset now = DateTimeOffset.UtcNow - TokenIssuer.Lifetime + left;

        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Holds every create at the door, so the number of batches in flight can be counted.</summary>
    protected sealed class HoldCreates : DelegatingHandler
    {
        private readonly TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int total;
        private int current;
        private int peak;

        public int Peak => Volatile.Read(ref peak);

        public void Release() => open.TrySetResult();

        public async Task Reached(int count, TimeSpan limit)
        {
            using var timer = new CancellationTokenSource(limit);
            while (Volatile.Read(ref total) < count)
            {
                timer.Token.ThrowIfCancellationRequested();
                await Task.Delay(10);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.EndsWith("/batches"))
                return await base.SendAsync(request, token);

            var now = Interlocked.Increment(ref current);
            Interlocked.Increment(ref total);
            InterlockedMax(ref peak, now);
            await open.Task.WaitAsync(token);
            Interlocked.Decrement(ref current);
            return await base.SendAsync(request, token);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while (value > (seen = Volatile.Read(ref target)))
                Interlocked.CompareExchange(ref target, value, seen);
        }
    }
}
