public sealed record Change(DateTimeOffset Time, string Kind, string Object, string? Old, string? New);

public sealed class ChangeFeed(SocketHub hub)
{
    private const int Keep = 500;
    private readonly Queue<Change> kept = new();

    public void Add(Change change)
    {
        lock (kept)
        {
            kept.Enqueue(change);
            if (kept.Count > Keep) kept.Dequeue();
        }
        hub.Broadcast(new { type = "change", change });
    }

    public void Add(string kind, string target, string? old, string? current) => Add(new Change(DateTimeOffset.UtcNow, kind, target, old, current));

    public object Frame()
    {
        lock (kept) return new { type = "changes", changes = kept.ToArray() };
    }
}
