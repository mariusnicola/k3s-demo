using System.Collections.Concurrent;

/// <summary>Keeps one stuck call from stopping the whole report.</summary>
public static class Bounded
{
    private static readonly TimeSpan ReportLimit = TimeSpan.FromSeconds(90);

    public static async Task<T> Within<T>(Func<Task<T>> call, TimeSpan limit, T fallback)
    {
        try
        {
            return await call().WaitAsync(limit);
        }
        catch (Exception failure)
        {
            Console.WriteLine($"[WARN] bounded call: {failure.Message.ReplaceLineEndings(" ")}");
            return fallback;
        }
    }

    public static bool IsStale(DateTimeOffset checkedAt, DateTimeOffset now) => now - checkedAt > ReportLimit;
}

/// <summary>Every background loop runs here, so none can end or fail without a log line and a failing check.</summary>
public sealed class Loops(Action<string> log, TimeSpan retry, Action<Check> failed)
{
    public const string CheckName = "monitor loops";
    private readonly ConcurrentDictionary<string, string> failing = new();

    public Task Forever(string name, Func<Task> cycle, CancellationToken stopping) => Run(name, cycle, true, stopping);

    /// <summary>
    /// For a cycle that follows a stream. It stays connected and may never return, or it may note its own failure and
    /// return to retry, so returning proves nothing. Only the action it is given clears a failure, once it receives.
    /// </summary>
    public Task Following(string name, Func<Action, Task> cycle, CancellationToken stopping) =>
        Run(name, () => cycle(() => Succeeded(name)), false, stopping);

    private async Task Run(string name, Func<Task> cycle, bool returnClears, CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested && await Turn(name, cycle, returnClears, stopping))
            {
            }
            log($"[OK] {name} loop ended: stopping");
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            log($"[OK] {name} loop ended: stopping");
        }
        catch (Exception failure)
        {
            var reason = failure.Message.ReplaceLineEndings(" ");
            log($"[ERROR] {name} loop ended: {reason}");
            Failed(name, $"loop ended: {reason}");
        }
    }

    /// <summary>One cycle. False once stopping has cut it short.</summary>
    private async Task<bool> Turn(string name, Func<Task> cycle, bool returnClears, CancellationToken stopping)
    {
        try
        {
            await cycle();
            if (returnClears) Succeeded(name);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception failure)
        {
            var reason = failure.Message.ReplaceLineEndings(" ");
            log($"[ERROR] {name} cycle failed: {reason}");
            Failed(name, reason);
            failed(Check());
            await Task.Delay(retry, stopping);
        }
        return true;
    }

    public void Failed(string name, string reason) => failing[name] = reason;

    public void Succeeded(string name) => failing.TryRemove(name, out _);

    public Check Check()
    {
        var errors = failing.Select(f => $"{f.Key}: {f.Value}").Order().ToList();
        return new Check(CheckName, errors.Count == 0, errors.Count == 0 ? null : string.Join("; ", errors));
    }

    /// <summary>A failed cycle publishes no report of its own, so the last one carries the failure, dated now.</summary>
    public static Report Mark(Report? last, Check failing, DateTimeOffset now)
    {
        if (last is null) return new Report(now, false, [failing], [], [], [], []);
        return last with { CheckedAt = now, Healthy = false, Checks = [.. last.Checks.Where(c => c.Name != CheckName), failing] };
    }
}
