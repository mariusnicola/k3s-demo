public sealed record OperationStep(string Name, string State, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);
public sealed record Operation(string Id, string Name, string Work, string? Step, DateTimeOffset StartedAt, DateTimeOffset LastSeenAt, string State, DateTimeOffset? EndedAt, OperationStep[] Steps, int? EtaSeconds);

/// <summary>The only place that decides what an operation's state is.</summary>
public sealed class Operations(SocketHub hub)
{
    public const string Running = "running";
    public const string NoSignal = "no-signal";
    public const string Done = "done";
    public const string Failed = "failed";
    private const string StepPending = "pending";
    private const string StepRunning = "running";
    private const string StepDone = "done";
    private const string StepFailed = "failed";
    private static readonly TimeSpan SignalLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan KeepDone = TimeSpan.FromSeconds(15);
    private readonly Dictionary<string, Operation> active = [];
    private readonly Dictionary<string, Dictionary<string, double>> lastGoodRun = [];

    public void Start(string id, string name, string work, DateTimeOffset at, IReadOnlyList<string>? steps = null) => Change(() =>
    {
        // A failed or silent run stays until the same work runs again, so nobody can miss it.
        foreach (var dead in active.Values.Where(o => o.Work == work && o.State is NoSignal or Failed).ToList()) active.Remove(dead.Id);
        var list = (steps ?? []).Select(s => new OperationStep(s, StepPending, null, null)).ToArray();
        active[id] = new Operation(id, name, work, null, at, at, Running, null, list, null);
    });

    public void Step(string id, string step, DateTimeOffset at) => Change(() =>
    {
        if (active.TryGetValue(id, out var operation)) active[id] = operation with { Step = step, LastSeenAt = at, State = Running, Steps = Advance(operation.Steps, step, at) };
    });

    public void Heartbeat(string id, DateTimeOffset at)
    {
        lock (active)
        {
            if (!active.TryGetValue(id, out var operation) || operation.EndedAt is not null) return;
            active[id] = operation with { LastSeenAt = at };
            if (operation.State == Running) return;
            active[id] = active[id] with { State = Running };
        }
        Push();
    }

    public void End(string id, bool ok, DateTimeOffset at) => Change(() =>
    {
        if (!active.TryGetValue(id, out var operation)) return;
        var steps = operation.Steps.Select(s => s.State == StepRunning ? s with { State = ok ? StepDone : StepFailed, EndedAt = at } : s).ToArray();
        active[id] = operation with { LastSeenAt = at, EndedAt = at, State = ok ? Done : Failed, Steps = steps };
        if (ok && steps.Length > 0) lastGoodRun[operation.Work] = steps.Where(s => s.State == StepDone).ToDictionary(s => s.Name, s => (s.EndedAt!.Value - s.StartedAt!.Value).TotalSeconds);
    });

    private static OperationStep[] Advance(OperationStep[] steps, string step, DateTimeOffset at)
    {
        if (steps.All(s => s.Name != step)) return steps;
        return steps.Select(s => s.Name == step ? s with { State = StepRunning, StartedAt = at }
            : s.State == StepRunning ? s with { State = StepDone, EndedAt = at }
            : s).ToArray();
    }

    /// <summary>Every step not finished counts in full, a running one too, so the figure changes only when a step ends.</summary>
    private int? Eta(Operation operation)
    {
        if (operation.EndedAt is not null || operation.Steps.Length == 0 || !lastGoodRun.TryGetValue(operation.Work, out var run)) return null;
        return (int)Math.Ceiling(operation.Steps.Where(s => s.State != StepDone).Sum(s => run.GetValueOrDefault(s.Name)));
    }

    /// <summary>A rollout has no script to send heartbeats, so each watch event for it counts as one.</summary>
    public void Rollout(string id, string name, bool progressing, DateTimeOffset at)
    {
        bool known;
        lock (active) known = active.TryGetValue(id, out var operation) && operation.EndedAt is null;
        if (progressing && known) Heartbeat(id, at);
        else if (progressing) Start(id, name, name, at);
        else if (known) End(id, true, at);
    }

    public void Tick(DateTimeOffset now)
    {
        var moved = false;
        lock (active)
        {
            foreach (var operation in active.Values.ToList())
            {
                if (operation.State == Running && now - operation.LastSeenAt > SignalLimit)
                {
                    active[operation.Id] = operation with { State = NoSignal };
                    moved = true;
                }
                if (operation.State == Done && now - operation.EndedAt > KeepDone)
                {
                    active.Remove(operation.Id);
                    moved = true;
                }
            }
        }
        if (moved) Push();
    }

    public object Frame()
    {
        lock (active) return new { type = "operations", operations = active.Values.OrderBy(o => o.StartedAt).Select(o => o with { EtaSeconds = Eta(o) }).ToArray() };
    }

    private void Change(Action apply)
    {
        lock (active) apply();
        Push();
    }

    private void Push() => hub.BroadcastPublic(Frame());
}
