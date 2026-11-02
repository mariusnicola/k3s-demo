using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>The paths a browser uses. Shared with tests/smoke/probe so both prove them the same way.</summary>
public static partial class BrowserPaths
{
    public const string SessionCookie = "bt_session";

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });

    [GeneratedRegex(@"(?:src|href)=""([^""]+\.(?:js|css)(?:\?[^""]*)?)""")]
    private static partial Regex Assets();

    public static async Task Page(string origin, string path, CancellationToken token)
    {
        using var page = await Http.GetAsync(origin + path, token);
        if (page.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException($"answered {(int)page.StatusCode}");
        if (!IsHtml(page)) throw new InvalidOperationException($"not HTML: {page.Content.Headers.ContentType}");

        var html = await page.Content.ReadAsStringAsync(token);
        var assets = Assets().Matches(html).Select(m => m.Groups[1].Value).Distinct().ToList();
        if (assets.Count == 0) throw new InvalidOperationException("names no JS or CSS");
        foreach (var asset in assets)
        {
            using var file = await Http.GetAsync(new Uri(new Uri(origin + path), asset), token);
            if (file.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException($"{asset} answered {(int)file.StatusCode}");
            // Web answers an unknown path with index.html and 200, so a lost asset looks like a success.
            if (IsHtml(file)) throw new InvalidOperationException($"{asset} answered with the HTML page");
        }
    }

    public static async Task Unauthorized(string origin, string path, CancellationToken token)
    {
        // Exactly 401: a lost route falls through to web, which answers 200.
        using var answer = await Http.GetAsync(origin + path, token);
        if (answer.StatusCode != HttpStatusCode.Unauthorized) throw new InvalidOperationException($"answered {(int)answer.StatusCode}, expected 401");
    }

    public static async Task<string> SignIn(string origin, string machineToken, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, origin + "/api/auth/machine");
        request.Headers.Add("X-Machine-Token", machineToken);
        using var answer = await Http.SendAsync(request, token);
        if (IsHtml(answer)) throw new InvalidOperationException($"route dropped: web answered /api/auth/machine with {(int)answer.StatusCode}");
        if (!answer.IsSuccessStatusCode) throw new InvalidOperationException($"machine sign-in refused, answered {(int)answer.StatusCode}");

        using var body = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(token));
        return body.RootElement.GetProperty("token").GetString() ?? throw new InvalidOperationException("machine sign-in returned no token");
    }

    /// <summary>A 403 shows the route, the sign-in and the permission check all work, and spends nothing on the failure ledger.</summary>
    public static async Task Forbidden(string origin, string path, string bearer, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, origin + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var answer = await Http.SendAsync(request, token);
        if (answer.StatusCode == HttpStatusCode.Forbidden) return;
        // Web answers an unknown path with index.html and 200.
        if (answer.StatusCode == HttpStatusCode.OK && IsHtml(answer)) throw new InvalidOperationException($"route dropped: web answered {path} with 200");
        if (answer.StatusCode == HttpStatusCode.OK) throw new InvalidOperationException($"{path} answered 200: the monitor holds a permission it must not have");
        throw new InvalidOperationException($"{path} answered {(int)answer.StatusCode}, expected 403");
    }

    /// <summary>A POST that never reached the API leaves no count in Prometheus, so the Line's own figures are the only sign of it.</summary>
    public static async Task BundlesWorking(string origin, string bearer, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, origin + "/line/bundles");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var answer = await Http.SendAsync(request, token);
        if (IsHtml(answer)) throw new InvalidOperationException($"route dropped: web answered /line/bundles with {(int)answer.StatusCode}");
        if (answer.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException($"/line/bundles answered {(int)answer.StatusCode}");

        using var body = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(token));
        var figures = body.RootElement;
        if (!figures.GetProperty("failing").GetBoolean()) return;
        throw new InvalidOperationException($"last bundle failed: {figures.GetProperty("lastFailure").GetString()}, {figures.GetProperty("lastItems").GetInt32()} items, {figures.GetProperty("failedRequests").GetInt64()} of {figures.GetProperty("sent").GetInt64()} posts failed");
    }

    public static async Task Upgraded(string url, string origin, string bearer, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", origin);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            await socket.ConnectAsync(new Uri(url), token);
        }
        catch (WebSocketException) when (socket.HttpStatusCode != 0)
        {
            var path = new Uri(url).AbsolutePath;
            throw new InvalidOperationException(socket.HttpStatusCode == HttpStatusCode.OK
                ? $"route dropped: web answered {path} with 200"
                : $"{path} answered {(int)socket.HttpStatusCode}, expected 101");
        }
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    /// <summary>An admin's first frame is the report, so a report that leaks to an anonymous socket fails here at once.</summary>
    public static async Task OperationsOnly(string url, string origin, CancellationToken token)
    {
        using var socket = await Connect(url, origin, null, token);
        using var first = JsonDocument.Parse(await Receive(socket, token));
        var kind = first.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
        if (kind is null) throw new InvalidOperationException("report reached an anonymous socket");
        if (kind != "operations") throw new InvalidOperationException($"{kind} frame reached an anonymous socket");
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    public static async Task Report(string url, string origin, string session, CancellationToken token)
    {
        using var socket = await Connect(url, origin, session, token);
        while (true)
        {
            using var frame = JsonDocument.Parse(await Receive(socket, token));
            if (frame.RootElement.TryGetProperty("type", out _)) continue;
            if (frame.RootElement.GetProperty("caps").GetArrayLength() == 0) throw new InvalidOperationException("report has no caps");
            break;
        }
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    /// <summary>Fails if the socket closes before the time is up, so it proves the proxy holds it open.</summary>
    public static async Task Hold(string url, string origin, TimeSpan time, CancellationToken token)
    {
        using var socket = await Connect(url, origin, null, token);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(time);
        try
        {
            while (true) await Receive(socket, limit.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
        }
    }

    /// <summary>With since, kept changes older than it do not count, so an earlier run cannot pass this one.</summary>
    public static async Task<string> Wait(string url, string origin, string session, Regex pattern, DateTimeOffset? since, CancellationToken token)
    {
        using var socket = await Connected(url, origin, session, token);
        while (true)
        {
            var frame = await Receive(socket, token);
            foreach (var part in Parts(frame, since))
                if (pattern.IsMatch(part)) return part;
        }
    }

    private static IEnumerable<string> Parts(string frame, DateTimeOffset? since)
    {
        using var document = JsonDocument.Parse(frame);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var value) ? value.GetString() : null;
        if (type is not ("change" or "changes"))
            return [frame];
        var changes = type == "change" ? [root.GetProperty("change")] : root.GetProperty("changes").EnumerateArray().ToArray();
        return changes.Where(c => since is null || c.GetProperty("time").GetDateTimeOffset() >= since).Select(c => c.GetRawText()).ToList();
    }

    /// <summary>Names a connect that never answered, which a cancelled wait would otherwise hide.</summary>
    private static async Task<ClientWebSocket> Connected(string url, string origin, string session, CancellationToken token)
    {
        try
        {
            return await Connect(url, origin, session, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"no connection to {url}");
        }
    }

    private static async Task<ClientWebSocket> Connect(string url, string origin, string? session, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        if (origin.Length > 0) socket.Options.SetRequestHeader("Origin", origin);
        if (session is not null) socket.Options.SetRequestHeader("Cookie", $"{SessionCookie}={session}");
        await socket.ConnectAsync(new Uri(url), token);
        return socket;
    }

    private static async Task<string> Receive(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4 * 1024 * 1024];
        var length = 0;
        while (true)
        {
            var part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), token);
            if (part.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("socket closed");
            length += part.Count;
            if (part.EndOfMessage) return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
        }
    }

    private static bool IsHtml(HttpResponseMessage response) => response.Content.Headers.ContentType?.MediaType == "text/html";
}
