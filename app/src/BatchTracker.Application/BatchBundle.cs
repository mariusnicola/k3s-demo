using System.Diagnostics;
using BatchTracker.Domain;

namespace BatchTracker.Application;

/// <summary>One step of a bundle, as its single route takes it.</summary>
public sealed record BundleItem(
    string? Kind, string? CorrelationId = null, string? FarmId = null, string? Id = null, string? Product = null, string? Key = null,
    string? To = null, bool? Passed = null, string? Reason = null, string? IfMatch = null);

public static class BundleKinds
{
    public const string Create = "create";
    public const string Advance = "advance";
    public const string Inspect = "inspect";
}

/// <summary>What one item came to, under the id it ran with.</summary>
public sealed record BundleAnswer(string CorrelationId, Outcome<Versioned> Outcome);

/// <summary>
/// Runs a list of steps as one read and one write, each item through the checks of its single route, so one failed item
/// leaves the others alone. A store failure ends the call: the caller repeats the list, which the id and If-Match make safe.
/// </summary>
public sealed class BatchBundle(IBatchStore store, IHopReporter hops, TimeProvider time, IBatchCounters counters)
{
    /// <summary>The Line's walks in flight, each with one step due at most.</summary>
    public const int MaxItems = 500;

    private readonly BatchWriteNews news = new(hops, time, counters);

    public async Task<IReadOnlyList<BundleAnswer>> RunAsync(Caller caller, IReadOnlyList<BundleItem> items, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var work = items.Select(item => new Work(item, Correlation.IsValid(item.CorrelationId) ? item.CorrelationId! : Correlation.New())).ToList();
            foreach (var one in work) Check(caller, one);
            await ApplyStepsAsync(caller, work, token);
            await WriteAsync(work, token);
            foreach (var one in work) Count(one);
            return work.Select(one => new BundleAnswer(one.CorrelationId, one.Outcome!)).ToList();
        }
        finally
        {
            counters.Time(Stages.Bundle, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private void Check(Caller caller, Work work)
    {
        using var action = Correlation.Use(work.CorrelationId);
        var item = work.Item;
        switch (item.Kind)
        {
            case BundleKinds.Create:
                PlanCreate(caller, work);
                break;
            case BundleKinds.Advance:
                TakeStep(work, BatchChecks.Advance(caller, item.FarmId ?? "", item.Id ?? "", item.To, item.IfMatch));
                break;
            case BundleKinds.Inspect when !caller.Permissions.Contains(Permissions.BatchInspect):
                work.Outcome = Outcome<Versioned>.Fail(Failure.Forbidden, "This step needs batch.inspect.");
                break;
            case BundleKinds.Inspect:
                TakeStep(work, BatchChecks.Inspect(caller, item.FarmId ?? "", item.Id ?? "", item.Passed, item.Reason, item.IfMatch));
                break;
            default:
                work.Outcome = BatchChecks.Bad($"Unknown kind {item.Kind}.");
                break;
        }
    }

    private void PlanCreate(Caller caller, Work work)
    {
        var item = work.Item;
        work.Outcome = BatchChecks.Create(caller, item.FarmId, item.Product, item.Key);
        if (work.Outcome is not null) return;
        var id = BatchChecks.IdOf(caller.User, item.Key);
        work.Write = new BundleWrite(BatchMachine.Create(id, item.FarmId!, item.Product!.Trim(), NewStamp(caller)), null);
    }

    private static void TakeStep(Work work, Outcome<MoveStep> step)
    {
        if (step.Value is null) work.Outcome = step.As<Versioned>();
        work.Step = step.Value;
    }

    /// <summary>One read for every step, then each row through its own rules.</summary>
    private async Task ApplyStepsAsync(Caller caller, List<Work> work, CancellationToken token)
    {
        var steps = work.Where(w => w.Step is not null).ToList();
        var keys = steps.Where(w => BatchChecks.IsBatchId(w.Step!.Id)).Select(w => new BatchKey(w.Step!.FarmId, w.Step.Id)).Distinct().ToList();
        var rows = (await store.GetManyAsync(keys, token)).ToDictionary(r => new BatchKey(r.Batch.FarmId, r.Batch.Id));
        foreach (var one in steps)
        {
            using var action = Correlation.Use(one.CorrelationId);
            var row = rows.GetValueOrDefault(new BatchKey(one.Step!.FarmId, one.Step.Id));
            var next = BatchChecks.Apply(row, one.Step, NewStamp(caller));
            if (next.Value is null) one.Outcome = next.As<Versioned>();
            else one.Write = new BundleWrite(next.Value, row!.ETag);
        }
    }

    private async Task WriteAsync(List<Work> work, CancellationToken token)
    {
        var writing = work.Where(w => w.Write is not null).ToList();
        var saved = await store.WriteManyAsync(writing.Select(w => w.Write!).ToList(), token);
        for (var i = 0; i < writing.Count; i++) Saw(writing[i], saved[i]);
        await SettleRepeatsAsync(writing.Where(w => w.Outcome is null).ToList(), token);
    }

    /// <summary>A refused create is left for the read that tells a repeat from a clash.</summary>
    private void Saw(Work work, Versioned? saved)
    {
        if (saved is null)
        {
            if (work.Write!.ETag is not null) work.Outcome = BatchChecks.Stale();
            return;
        }
        using var action = Correlation.Use(work.CorrelationId);
        Correlation.About(saved.Batch.FarmId, saved.Batch.Id, saved.Batch.State.ToString());
        if (work.Write!.ETag is null) news.Created(saved.Batch);
        else news.Moved(saved.Batch);
        work.Outcome = Outcome<Versioned>.Ok(saved);
    }

    private async Task SettleRepeatsAsync(List<Work> repeats, CancellationToken token)
    {
        if (repeats.Count == 0) return;
        var keys = repeats.Select(w => new BatchKey(w.Write!.Batch.FarmId, w.Write.Batch.Id)).Distinct().ToList();
        var held = (await store.GetManyAsync(keys, token)).ToDictionary(r => new BatchKey(r.Batch.FarmId, r.Batch.Id));
        foreach (var one in repeats)
            one.Outcome = BatchChecks.Repeated(held.GetValueOrDefault(new BatchKey(one.Write!.Batch.FarmId, one.Write.Batch.Id)), one.Write.Batch.Product);
    }

    /// <summary>A farm nobody knows has no series, and the counter drops it.</summary>
    private void Count(Work work)
    {
        var farm = work.Item.FarmId ?? "";
        counters.Count(MetricNames.BundleItems, farm);
        if (work.Outcome!.Failure is not null) counters.Count(MetricNames.BundleItemFailures, farm);
    }

    private Stamp NewStamp(Caller caller) => new(caller.User, Guid.NewGuid().ToString("N"), time.GetUtcNow(), Correlation.Current);

    private sealed class Work(BundleItem item, string correlationId)
    {
        public BundleItem Item { get; } = item;
        public string CorrelationId { get; } = correlationId;
        public MoveStep? Step { get; set; }
        public BundleWrite? Write { get; set; }
        public Outcome<Versioned>? Outcome { get; set; }
    }
}
