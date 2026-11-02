using System.Text;
using System.Text.Json;

/// <summary>Steps Kubernetes cannot see arrive as JSON lines in a read-only file, so no endpoint is added.</summary>
public sealed class FileTail(string path, ChangeFeed feed, Operations operations, Loops loops)
{
    private const string OldDeployName = "Deploy the cluster and the app";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private long offset;
    private string partial = "";

    public Task Run(CancellationToken token) => loops.Forever("change file", async () =>
    {
        try
        {
            ReadNew();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[WARN] change file: {failure.Message}");
        }
        await Task.Delay(Interval, token);
    }, token);

    private static string[]? Steps(JsonElement root) =>
        root.TryGetProperty("steps", out var steps) ? steps.EnumerateArray().Select(s => s.GetString()!).ToArray() : null;

    /// <summary>Deploy lines written before `work` existed replay forever, and only a shared work lets a later deploy clear them.</summary>
    private static string WorkOf(string name) => name == OldDeployName ? "deploy" : name;

    private void ReadNew()
    {
        if (!File.Exists(path)) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < offset) offset = 0;
        if (stream.Length == offset) return;
        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[stream.Length - offset];
        stream.ReadExactly(bytes);
        offset += bytes.Length;

        var lines = (partial + Encoding.UTF8.GetString(bytes)).Split('\n');
        partial = lines[^1];
        foreach (var line in lines[..^1].Where(l => l.Trim().Length > 0)) Apply(line);
    }

    private void Apply(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var at = root.GetProperty("time").GetDateTimeOffset();
            string? Text(string name) => root.TryGetProperty(name, out var value) ? value.GetString() : null;
            switch (Text("type"))
            {
                case "change": feed.Add(new Change(at, Text("kind") ?? "setup", Text("object") ?? "", Text("old"), Text("new"))); break;
                case "start": operations.Start(Text("id")!, Text("name")!, Text("work") ?? WorkOf(Text("name")!), at, Steps(root)); break;
                case "step": operations.Step(Text("id")!, Text("step")!, at); feed.Add(new Change(at, "setup step", Text("id")!, null, Text("step"))); break;
                case "heartbeat": operations.Heartbeat(Text("id")!, at); break;
                case "end": operations.End(Text("id")!, root.GetProperty("ok").GetBoolean(), at); break;
            }
        }
        catch (Exception failure) when (failure is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or NullReferenceException)
        {
            Console.WriteLine($"[WARN] change line skipped: {failure.Message}");
        }
    }
}
