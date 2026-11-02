using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using Microsoft.Extensions.Logging.Abstractions;

namespace BatchTracker.UnitTests;

/// <summary>What the tests of LineFailureTests and its neighbours share.</summary>
public abstract class LineFailureTest : IDisposable
{
    protected const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZGEifQ.c2lnbmF0dXJl";
    protected const string BatchId = "11111111-1111-1111-1111-111111111111";

    protected readonly LineLog log = new();
    private readonly List<LineEngine> engines = [];

    /// <summary>Started, because its bundles only go out while it runs. Rate 0 starts no walk of its own.</summary>
    protected LineEngine Engine(Func<HttpRequestMessage, Task<HttpResponseMessage>> route, LineOptions? options = null, LineSettled? settled = null)
    {
        var http = new HttpClient(new Wire(route)) { BaseAddress = new Uri("http://api.test/api/") };
        var chosen = options ?? new LineOptions { PollDelay = TimeSpan.FromMilliseconds(1) };
        var lineOptions = chosen with { BundleEvery = TimeSpan.FromMilliseconds(1) };
        var api = new LineApi(http, lineOptions, TimeProvider.System, settled ?? new LineSettled());
        var engine = new LineEngine(api, log, lineOptions, TimeProvider.System, NullLogger<LineEngine>.Instance);
        engine.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        engines.Add(engine);
        return engine;
    }

    public void Dispose()
    {
        foreach (var engine in engines) engine.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Answers a bundle by asking the route once per item as its single call, so a test states what the API says to a step and
    /// stays true for either way of sending it. Anything but a JSON answer goes back whole, as a broken reply to the POST.
    /// </summary>
    protected sealed class Wire(Func<HttpRequestMessage, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/batches/bundle")) return await route(request);

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var results = new List<object>();
            foreach (var item in body.RootElement.GetProperty("items").EnumerateArray())
            {
                var answer = await route(Single(item));
                if (await ResultOfAsync(answer, token) is not { } result) return answer;
                results.Add(result);
            }
            return await Json(HttpStatusCode.OK, new { results });
        }

        private static HttpRequestMessage Single(JsonElement item)
        {
            var kind = item.GetProperty("kind").GetString();
            var path = kind == "create" ? "batches" : $"batches/{item.GetProperty("farmId").GetString()}/{item.GetProperty("id").GetString()}/{kind}";
            var single = new HttpRequestMessage(HttpMethod.Post, "http://api.test/api/" + path) { Content = new StringContent(item.GetRawText()) };
            if (kind != "create") single.Headers.TryAddWithoutValidation("If-Match", item.GetProperty("ifMatch").GetString());
            return single;
        }

        private static async Task<object?> ResultOfAsync(HttpResponseMessage answer, CancellationToken token)
        {
            var text = await answer.Content.ReadAsStringAsync(token);
            try
            {
                using var json = JsonDocument.Parse(text);
                var status = (int)answer.StatusCode;
                if (!answer.IsSuccessStatusCode) return new { status, detail = json.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : answer.ReasonPhrase };
                return new { status, etag = answer.Headers.ETag?.ToString(), batch = json.RootElement.Clone() };
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    protected static Task<HttpResponseMessage> Json(HttpStatusCode code, object body, string? etag = null)
    {
        var response = new HttpResponseMessage(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonDefaults.Options), Encoding.UTF8, "application/json")
        };
        if (etag is not null) response.Headers.TryAddWithoutValidation("ETag", etag);
        return Task.FromResult(response);
    }

    protected static Batch NewBatch(string? pendingStatus = PendingStatus.Sent)
    {
        var stamp = new Stamp("ada", "trace-1", DateTimeOffset.UtcNow);
        var batch = BatchMachine.Create(BatchId, "farm-b", "p", stamp);
        return pendingStatus is null
            ? batch with { Pending = null }
            : batch with { Pending = batch.Pending! with { Status = pendingStatus } };
    }

    protected static bool IsLogin(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.EndsWith("auth/machine");
    protected static bool IsCreate(HttpRequestMessage r) => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/batches");

    protected static Task<HttpResponseMessage> Login() => Json(HttpStatusCode.OK, new { token = Token });

    /// <summary>Login and create work; every later call answers as `then` says.</summary>
    protected static Func<HttpRequestMessage, Task<HttpResponseMessage>> AfterCreate(Func<HttpRequestMessage, Task<HttpResponseMessage>> then) => request =>
        IsLogin(request) ? Login()
        : IsCreate(request) ? Json(HttpStatusCode.Created, NewBatch(null))
        : then(request);

    protected LineEntry OneError()
    {
        var page = log.Query(new LogQuery(Level: LineLevel.Error));
        return Assert.Single(page.Entries);
    }

    public static TheoryData<Exception> NetworkFailures() => new()
    {
        new HttpRequestException("Connection refused"),
        new SocketException((int)SocketError.ConnectionReset),
        new IOException("The response ended prematurely."),
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.")
    };
}
