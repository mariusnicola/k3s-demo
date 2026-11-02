using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Live;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BatchTracker.IntegrationTests;

/// <summary>What the tests of LiveTests and its neighbours share.</summary>
public abstract class LiveTest(Stack stack) : StackTest(stack)
{
    protected static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    protected const string Path = "/ws/hops";

    protected readonly LiveHost live = new();

    public override async Task DisposeAsync() => await live.DisposeAsync();

    protected TestServer Server
    {
        get
        {
            live.CreateClient();
            return live.Server;
        }
    }

    protected static Hop NewHop(string farm, string trace) =>
        new(trace, "b", farm, HopNames.Api, "Created", DateTimeOffset.UtcNow, true);

    protected Task<WebSocket> OpenAsync(string user) => Sockets.OpenAsync(Server, Path, Sockets.CookieFor(user));

    protected async Task<WebSocket> ConnectedAsync(string user)
    {
        var socket = await OpenAsync(user);
        Assert.Equal("snapshot", (await Sockets.ReceiveAsync(socket)).GetProperty("type").GetString());
        return socket;
    }

    /// <summary>The first scan runs behind the started pod, so a test that needs its result waits for it.</summary>
    protected async Task WaitForFirstScanAsync()
    {
        using var socket = await OpenAsync("ada");
        var frame = await Sockets.ReceiveTypeAsync(socket, "snapshot");
        while (frame.GetProperty("reconciledAt").ValueKind == JsonValueKind.Null)
            frame = await Sockets.ReceiveTypeAsync(socket, "summary");
    }

    protected Task PostAsync(Hop hop) => live.CreateClient().Internal().PostAsJsonAsync("/hops", hop, JsonDefaults.Options);

    protected Task PostChangeAsync(Versioned row) => live.CreateClient().Internal().PostAsJsonAsync("/changes", row, JsonDefaults.Options);

    protected static Versioned Row(string farm, long version = 1, BatchState state = BatchState.Created, string? id = null) => new(
        new Batch
        {
            Id = id ?? Guid.NewGuid().ToString(),
            FarmId = farm,
            Product = "Vaccine",
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = version
        },
        $"\"etag-{version}\"");

    protected sealed class ExpiresIn(TimeSpan left) : TimeProvider
    {
        private readonly DateTimeOffset now = DateTimeOffset.UtcNow - TokenIssuer.Lifetime + left;

        public override DateTimeOffset GetUtcNow() => now;
    }

    protected sealed class BlockingHandler(ManualResetEventSlim entered, ManualResetEventSlim release) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            entered.Set();
            release.Wait(CancellationToken.None);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
