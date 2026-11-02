using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Azure.Messaging.ServiceBus;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;

namespace BatchTracker.Infrastructure;

public sealed record WorkerRoute(string InternalToken, string? Host);

/// <summary>HttpClient timeout must stay off: each call carries its own limit.</summary>
public sealed class HttpWorkerClient(HttpClient http, WorkerRoute route) : IWorkerClient
{
    public const string TokenHeader = "X-Internal-Token";

    /// <summary>Safe to repeat: the recorder skips an event it already handled.</summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(4);
    /// <summary>Safe to repeat: a batch already flagged is not flagged again, and each write is guarded by its ETag.</summary>
    private static readonly TimeSpan NightlyTimeout = TimeSpan.FromMinutes(5);

    public async Task<WorkerResult> PostEventAsync(BatchEvent message, CancellationToken token)
    {
        using var request = NewRequest(HttpMethod.Post, "events").SafeToRepeat().WithAttemptTimeout(DeliveryTimeout);
        request.Content = JsonContent.Create(message, options: JsonDefaults.Options);
        try
        {
            using var response = await http.SendAsync(request, token);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => WorkerResult.Done,
                HttpStatusCode.UnprocessableEntity => WorkerResult.Refused,
                HttpStatusCode.Conflict => WorkerResult.Contended,
                _ => WorkerResult.Unavailable
            };
        }
        catch (Exception e) when (!token.IsCancellationRequested && e is HttpRequestException or OperationCanceledException)
        {
            return WorkerResult.Unavailable;
        }
    }

    public async Task<bool> ReadyAsync(CancellationToken token)
    {
        using var request = NewRequest(HttpMethod.Get, "ready").SingleAttempt().WithAttemptTimeout(ReadyTimeout);
        try
        {
            using var response = await http.SendAsync(request, token);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception e) when (!token.IsCancellationRequested && e is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<ComplianceResult> RunNightlyAsync(CancellationToken token)
    {
        using var request = NewRequest(HttpMethod.Post, "nightly/run").SafeToRepeat().WithAttemptTimeout(NightlyTimeout);
        using var response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ComplianceResult>(JsonDefaults.Options, token))!;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TokenHeader, route.InternalToken);

        // The ingress routes Worker calls by Host.
        if (route.Host is not null) request.Headers.Host = route.Host;
        return request;
    }
}

/// <summary>The pod this service puts on its hops. Registered only by services that draw one element per pod.</summary>
public sealed record HopSource(string Pod);

public sealed class TcpBusProbe : IBusProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private const int DefaultAmqpPort = 5671;

    private readonly string host;
    private readonly int port;

    public TcpBusProbe(string connectionString)
    {
        var endpoint = ServiceBusConnectionStringProperties.Parse(connectionString).Endpoint;
        host = endpoint.Host;
        port = endpoint.IsDefaultPort ? DefaultAmqpPort : endpoint.Port;
    }

    public async Task<bool> ReachableAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Timeout);
        using var socket = new TcpClient();
        try
        {
            await socket.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}
