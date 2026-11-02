using System.Collections.Concurrent;
using BatchTracker.Application;
using BatchTracker.Domain;

namespace BatchTracker.Line;

/// <summary>One item's answer, shaped as its single call's. Attempts are the whole POST's.</summary>
public sealed record LineAnswer(int Status, string? ETag, Batch? Batch, string? Detail, int Attempts);

/// <summary>What the API said to a POST. Failed means the POST itself, so every answer holds its status.</summary>
public sealed record LineBundleReply(IReadOnlyList<LineAnswer> Answers, bool Failed);

public sealed record LineDue(BundleItem Item, TaskCompletionSource<LineAnswer> Answer, CancellationToken Cancel);

/// <summary>The steps due. A step adds its item and awaits the answer the next POST brings.</summary>
public sealed class LineBundle
{
    private readonly ConcurrentQueue<LineDue> due = new();

    public Task<LineAnswer> AddAsync(BundleItem item, CancellationToken cancel)
    {
        var answer = new TaskCompletionSource<LineAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        due.Enqueue(new LineDue(item, answer, cancel));
        return answer.Task.WaitAsync(cancel);
    }

    /// <summary>All that is due now. A walk that was cancelled sends nothing, as it would have called nothing.</summary>
    public IReadOnlyList<LineDue> Take()
    {
        var taken = new List<LineDue>();
        for (var left = due.Count; left > 0 && due.TryDequeue(out var one); left--)
            if (!one.Cancel.IsCancellationRequested) taken.Add(one);
        return taken;
    }
}

/// <summary>
/// One POST a tick, carrying all that is due. A POST running past the tick holds the next one, and what comes due meanwhile
/// waits, bounded by the walks in flight.
/// </summary>
public sealed class LineBundleSender(LineApi api, LineOptions options, TimeProvider time, ILogger logger)
{
    /// <summary>Ends with its token.</summary>
    public async Task RunAsync(CancellationToken cancel)
    {
        using var timer = new PeriodicTimer(options.BundleEvery, time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancel))
                if (api.Bundle.Take() is { Count: > 0 } due) await SendAsync(due, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
    }

    /// <summary>Every due item gets an answer or the failure, so no walk waits on a POST that is over.</summary>
    private async Task SendAsync(IReadOnlyList<LineDue> due, CancellationToken cancel)
    {
        using var action = Correlation.Use(Correlation.New());
        var started = time.GetTimestamp();
        try
        {
            var reply = await api.PostBundleAsync(due.Select(d => d.Item).ToList(), cancel);
            for (var i = 0; i < due.Count; i++) due[i].Answer.TrySetResult(reply.Answers[i]);
            Record(reply, ElapsedMs(started));
        }
        catch (Exception e)
        {
            foreach (var one in due) one.Answer.TrySetException(e);
            if (cancel.IsCancellationRequested) return;
            api.Figures.Failed(due.Count, e.GetType().Name, ElapsedMs(started));
            logger.LogWarning("Line bundle of {Items} items failed: {Type}", due.Count, e.GetType().Name);
        }
    }

    private void Record(LineBundleReply reply, long ms)
    {
        if (reply.Failed)
        {
            var status = reply.Answers[0].Status;
            api.Figures.Failed(reply.Answers.Count, $"HTTP {status}", ms);
            logger.LogWarning("Line bundle of {Items} items refused with HTTP {Status}", reply.Answers.Count, status);
            return;
        }
        api.Figures.Worked(reply.Answers.Count, reply.Answers.Count(a => a.Batch is null), ms);
    }

    private long ElapsedMs(long started) => (long)time.GetElapsedTime(started).TotalMilliseconds;
}

/// <summary>What the bundles came to. LastFailure names a status or a type, never text from elsewhere, so any signed-in caller may read it.</summary>
public sealed record LineBundleReading(
    long Sent, long Items, long FailedItems, long FailedRequests, int LastItems, long LastMs, string? LastFailure, bool Failing);

/// <summary>The Line is outside Prometheus, so a POST that never reached the API is counted here or nowhere.</summary>
public sealed class LineBundleFigures(TimeProvider time)
{
    /// <summary>A stopped Line sends nothing to clear a failure, so it ages out.</summary>
    private static readonly TimeSpan FailingFor = TimeSpan.FromSeconds(120);

    private readonly object sync = new();
    private LineBundleReading held = new(0, 0, 0, 0, 0, 0, null, false);
    private DateTimeOffset failedAt;

    public void Worked(int items, int failedItems, long ms)
    {
        lock (sync)
        {
            held = held with
            {
                Sent = held.Sent + 1, Items = held.Items + items, FailedItems = held.FailedItems + failedItems,
                LastItems = items, LastMs = ms, LastFailure = null
            };
        }
    }

    public void Failed(int items, string reason, long ms)
    {
        lock (sync)
        {
            failedAt = time.GetUtcNow();
            held = held with
            {
                Sent = held.Sent + 1, Items = held.Items + items, FailedItems = held.FailedItems + items, FailedRequests = held.FailedRequests + 1,
                LastItems = items, LastMs = ms, LastFailure = reason
            };
        }
    }

    public LineBundleReading Reading()
    {
        lock (sync)
        {
            return held with { Failing = held.LastFailure is not null && time.GetUtcNow() - failedAt < FailingFor };
        }
    }
}
