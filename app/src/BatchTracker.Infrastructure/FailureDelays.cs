using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BatchTracker.Infrastructure;

public sealed record FailureRules
{
    public int FreeFailures { get; init; } = 2;
    public TimeSpan FirstDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Quiet { get; init; } = TimeSpan.FromMinutes(15);
    public int MaxWaiting { get; init; } = 10;
    public int RefuseFrom { get; init; } = 10;
    public int MaxClients { get; init; } = 10_000;
}

/// <summary>
/// One count per client per pod, so two pods let a client fail twice as often before each delay.
/// Only a quiet period ends a count: one address can hold many clients, so a sign-in proves nothing about who failed.
/// </summary>
public sealed class FailureLedger(FailureRules rules, TimeProvider time)
{
    private sealed class Entry
    {
        public long Failures;
        public DateTimeOffset LastFailure;
        public int Waiting;
    }

    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = [];

    public int Clients
    {
        get
        {
            lock (sync) return entries.Count;
        }
    }

    public long FailuresOf(string client)
    {
        lock (sync) return entries.TryGetValue(client, out var entry) ? entry.Failures : 0;
    }

    public int WaitingOf(string client)
    {
        lock (sync) return entries.TryGetValue(client, out var entry) ? entry.Waiting : 0;
    }

    /// <summary>Taken before the route runs, so a burst cannot pass the cap before the first of it fails.</summary>
    public bool TryEnter(string client, out TimeSpan retryAfter)
    {
        lock (sync)
        {
            var entry = Current(client);
            if (entry.Waiting >= rules.MaxWaiting)
            {
                var wait = DelayFor(entry.Failures);
                retryAfter = wait >= TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1);
                return false;
            }
            entry.Waiting++;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public void Leave(string client)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(client, out var entry)) return;
            entry.Waiting--;
            RemoveIfEmpty(client, entry);
        }
    }

    /// <summary>Frees a seat when the delay ends, on a timer, so a client that leaves early buys no speed and no thread waits.</summary>
    public void LeaveAfter(string client, TimeSpan delay)
    {
        ITimer? timer = null;
        timer = time.CreateTimer(_ =>
        {
            Leave(client);
            timer?.Dispose();
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>True when the next failure would reach the limit. The wait is what is left until the count can clear.</summary>
    public bool IsRefused(string client, out TimeSpan retryAfter)
    {
        lock (sync)
        {
            retryAfter = TimeSpan.Zero;
            if (!entries.TryGetValue(client, out var entry) || IsQuiet(entry) || entry.Failures + 1 < rules.RefuseFrom) return false;
            retryAfter = rules.Quiet - (time.GetUtcNow() - entry.LastFailure);
            return true;
        }
    }

    public TimeSpan Fail(string client)
    {
        lock (sync)
        {
            var entry = Current(client);
            entry.Failures++;
            entry.LastFailure = time.GetUtcNow();
            return DelayFor(entry.Failures);
        }
    }

    private Entry Current(string client)
    {
        if (!entries.TryGetValue(client, out var entry))
        {
            if (entries.Count >= rules.MaxClients) Evict();
            return entries[client] = new Entry();
        }
        if (IsQuiet(entry)) entry.Failures = 0;
        return entry;
    }

    private bool IsQuiet(Entry entry) => entry.Failures > 0 && time.GetUtcNow() - entry.LastFailure >= rules.Quiet;

    private void RemoveIfEmpty(string client, Entry entry)
    {
        if (entry.Failures == 0 && entry.Waiting == 0) entries.Remove(client);
    }

    /// <summary>Quiet clients go first, then the fewest failures, so a passing crowd cannot push an attacker out.</summary>
    private void Evict()
    {
        var free = entries.Where(e => e.Value.Waiting == 0).ToList();
        var quiet = free.Where(e => IsQuiet(e.Value)).ToList();
        if (quiet.Count > 0)
        {
            foreach (var entry in quiet) entries.Remove(entry.Key);
            return;
        }
        if (free.Count == 0) return;
        entries.Remove(free.MinBy(e => (e.Value.Failures, e.Value.LastFailure)).Key);
    }

    private TimeSpan DelayFor(long failures)
    {
        var steps = failures - rules.FreeFailures;
        if (steps <= 0) return TimeSpan.Zero;

        // Capped before the power, so a long run cannot overflow the delay.
        var doubled = Math.Pow(2, Math.Min(steps - 1, 30));
        var delay = rules.FirstDelay.TotalMilliseconds * doubled;
        return TimeSpan.FromMilliseconds(Math.Min(delay, rules.MaxDelay.TotalMilliseconds));
    }
}
