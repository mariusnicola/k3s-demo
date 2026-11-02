using System.Net;
using System.Text;
using k8s;

/// <summary>
/// Answers a Kubernetes client without a cluster. A path gives its replies in order and the last one stands.
/// A path nobody set gets a 404, as a missing object does.
/// </summary>
public sealed class FakeCluster : DelegatingHandler
{
    private sealed record Reply(HttpStatusCode Status, string Body, bool Hangs);

    private readonly Dictionary<string, Queue<Reply>> replies = [];

    public Kubernetes Client() => new(new KubernetesClientConfiguration { Host = "http://kube.test" }, this);

    public void Answer(string path, params string[] bodies)
    {
        foreach (var body in bodies) Add(path, new Reply(HttpStatusCode.OK, body, false));
    }

    public void Fail(string path, HttpStatusCode status = HttpStatusCode.InternalServerError) =>
        Add(path, new Reply(status, "{}", false));

    public void Reset(string path)
    {
        lock (replies) replies.Remove(path);
    }

    public void Hang(string path) => Add(path, new Reply(HttpStatusCode.OK, "{}", true));

    /// <summary>The key a watch on this path answers under.</summary>
    public static string Watch(string path) => path + "?watch";

    public static string List(params string[] items) =>
        $$"""{"metadata":{"resourceVersion":"1"},"items":[{{string.Join(",", items)}}]}""";

    public static string Node(string name, string ready = "True") =>
        """{"metadata":{"name":"@name"},"status":{"conditions":[{"type":"Ready","status":"@ready"}],"allocatable":{"memory":"2048Mi"}}}"""
            .Replace("@name", name).Replace("@ready", ready);

    public static string Pod(string space, string name, string phase = "Running", bool deleted = false)
    {
        var ending = deleted ? ""","deletionTimestamp":"2026-01-01T00:00:00Z" """ : "";
        return """
            {"metadata":{"namespace":"@space","name":"@name"@ending},
             "spec":{"nodeName":"w1","containers":[{"name":"c","resources":{"limits":{"cpu":"500m","memory":"256Mi"}}}]},
             "status":{"phase":"@phase","containerStatuses":[{"name":"c","ready":true,"image":"i","imageID":"i","restartCount":0}]}}
            """.Replace("@space", space).Replace("@name", name).Replace("@ending", ending).Replace("@phase", phase);
    }

    public static string Event(string type, string item) => $$"""{"type":"{{type}}","object":{{item.ReplaceLineEndings("")}}}""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimEnd('/');
        var reply = Next(request.RequestUri.Query.Contains("watch=true") ? Watch(path) : path);
        if (reply.Hangs) await Task.Delay(Timeout.Infinite, token);
        return new HttpResponseMessage(reply.Status)
        {
            RequestMessage = request,
            Content = new StringContent(reply.Body, Encoding.UTF8, "application/json")
        };
    }

    private void Add(string path, Reply reply)
    {
        lock (replies)
        {
            if (!replies.TryGetValue(path, out var queue)) replies[path] = queue = new Queue<Reply>();
            queue.Enqueue(reply);
        }
    }

    private Reply Next(string key)
    {
        lock (replies)
        {
            if (!replies.TryGetValue(key, out var queue)) return new Reply(HttpStatusCode.NotFound, "{}", false);
            return queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        }
    }
}
