using BatchTracker.Domain;

namespace BatchTracker.Application;

public sealed class BatchService(
IBatchStore store, IHopReporter hops, TimeProvider time, IBatchCounters counters)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxReasonLength = BatchChecks.MaxReasonLength;
    public const string IdempotencyHeader = BatchChecks.IdempotencyHeader;
    private const int WriteAttempts = 3;

    private readonly BatchWriteNews news = new(hops, time, counters);

    public async Task<Outcome<Versioned>> CreateAsync(Caller caller, string? farmId, string? product, string? key, CancellationToken token)
    {
        if (BatchChecks.Create(caller, farmId, product, key) is { } refused) return refused;

        var id = BatchChecks.IdOf(caller.User, key);
        var name = product!.Trim();
        Correlation.About(farmId!, id, nameof(BatchState.Created));
        var saved = await store.CreateAsync(BatchMachine.Create(id, farmId!, name, NewStamp(caller)), token);
        if (saved is null) return BatchChecks.Repeated(await store.GetAsync(farmId!, id, token), name);
        news.Created(saved.Batch);
        return Outcome<Versioned>.Ok(saved);
    }

    public async Task<Outcome<Versioned>> GetAsync(Caller caller, string farmId, string id, CancellationToken token)
    {
        if (!caller.HasFarm(farmId)) return BatchChecks.NoFarm(farmId);
        var row = await ReadAsync(farmId, id, token);
        return row is null ? BatchChecks.Missing() : Outcome<Versioned>.Ok(row);
    }

    public async Task<Outcome<IReadOnlyList<Batch>>> ListAsync(
        Caller caller, string? state, string? farmId, int? limit, CancellationToken token)
    {
        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
            return Outcome<IReadOnlyList<Batch>>.Fail(Failure.Validation, $"limit must be 1 to {MaxLimit}.");

        BatchState? wanted = null;
        if (!string.IsNullOrEmpty(state))
        {
            if (!BatchChecks.TryParseState(state, out var parsed))
                return Outcome<IReadOnlyList<Batch>>.Fail(Failure.Validation, $"Unknown state {state}.");
            wanted = parsed;
        }
        if (farmId is not null && !caller.HasFarm(farmId))
            return Outcome<IReadOnlyList<Batch>>.Fail(Failure.Forbidden, $"No access to farm {farmId}.");

        var farms = farmId is null ? caller.FarmScope : [farmId];
        var rows = await store.ListAsync(new BatchFilter(farms, wanted, size), token);
        return Outcome<IReadOnlyList<Batch>>.Ok(rows.Select(r => r.Batch).ToList());
    }

    public Task<Outcome<Versioned>> AdvanceAsync(
        Caller caller, string farmId, string id, string? to, string? ifMatch, CancellationToken token) =>
        MoveAsync(caller, BatchChecks.Advance(caller, farmId, id, to, ifMatch), token);

    public Task<Outcome<Versioned>> InspectAsync(
        Caller caller, string farmId, string id, bool? passed, string? reason, string? ifMatch, CancellationToken token) =>
        MoveAsync(caller, BatchChecks.Inspect(caller, farmId, id, passed, reason, ifMatch), token);

    public Task<Outcome<Versioned>> FailNextAsync(Caller caller, string farmId, string id, CancellationToken token) =>
        ChangeAsync(caller, farmId, id, batch => Outcome<Batch>.Ok(batch with { FailNext = true }), token);

    public async Task<Outcome<Versioned>> ReplayAsync(Caller caller, string farmId, string id, CancellationToken token)
    {
        var outcome = await ChangeAsync(caller, farmId, id, batch => batch.IsBlocked()
            ? Outcome<Batch>.Ok(batch with
            {
                FailNext = false,
                Pending = batch.Pending! with
                {
                    Status = PendingStatus.Pending,
                    StoredAt = null,
                    SentAt = null,
                    Error = null,
                    CorrelationId = Correlation.Current,
                    ReplayOf = batch.Pending.CorrelationId
                }
            })
            : Outcome<Batch>.Fail(Failure.Conflict, "Only a blocked batch can be replayed."), token);
        if (outcome.Value is not null) counters.Count(MetricNames.EventsReplayed, farmId);
        return outcome;
    }

    /// <summary>From the bounded scan, because reading every batch ever written outlasts a request. Past the scan's cap of open rows, pending, blocked and flagged read short.</summary>
    public async Task<Summary> SummaryAsync(Caller caller, CancellationToken token)
    {
        var scan = await store.FindForLiveAsync(token);
        return Reports.SummaryOf(Visible(scan, caller)) with { ByState = Reports.ByState(scan.Counts, caller) };
    }

    public async Task<IReadOnlyList<Alert>> AlertsAsync(Caller caller, CancellationToken token) =>
        Reports.AlertsOf(Visible(await store.FindForLiveAsync(token), caller));

    private static List<Batch> Visible(LiveScan scan, Caller caller) =>
        scan.Rows.Select(r => r.Batch).Where(b => caller.HasFarm(b.FarmId)).ToList();

    private async Task<Outcome<Versioned>> MoveAsync(Caller caller, Outcome<MoveStep> checkedStep, CancellationToken token)
    {
        if (checkedStep.Value is not { } step) return checkedStep.As<Versioned>();

        var row = await ReadAsync(step.FarmId, step.Id, token);
        var next = BatchChecks.Apply(row, step, NewStamp(caller));
        if (next.Value is null) return next.As<Versioned>();

        Correlation.About(row!.Batch.FarmId, row.Batch.Id, next.Value.State.ToString());
        var saved = await store.ReplaceAsync(next.Value, row.ETag, token);
        if (saved is null) return BatchChecks.Stale();
        news.Moved(saved.Batch);
        return Outcome<Versioned>.Ok(saved);
    }

    /// <summary>Admin changes carry no If-Match, so a race with the Dispatcher or Worker retries on fresh data.</summary>
    private async Task<Outcome<Versioned>> ChangeAsync(
        Caller caller, string farmId, string id, Func<Batch, Outcome<Batch>> change, CancellationToken token)
    {
        if (!caller.HasFarm(farmId)) return BatchChecks.NoFarm(farmId);
        for (var attempt = 0; attempt < WriteAttempts; attempt++)
        {
            var row = await ReadAsync(farmId, id, token);
            if (row is null) return BatchChecks.Missing();
            var changed = change(row.Batch);
            if (changed.Value is null) return Outcome<Versioned>.Fail(changed.Failure!.Value, changed.Detail!);
            Correlation.About(row.Batch.FarmId, row.Batch.Id, changed.Value.State.ToString());
            var saved = await store.ReplaceAsync(changed.Value, row.ETag, token);
            if (saved is not null) return Outcome<Versioned>.Ok(saved);
        }
        return Outcome<Versioned>.Fail(Failure.Conflict, "Batch kept changing, try again.");
    }

    private Task<Versioned?> ReadAsync(string farmId, string id, CancellationToken token) =>
        BatchChecks.IsBatchId(id) ? store.GetAsync(farmId, id, token) : Task.FromResult<Versioned?>(null);

    private Stamp NewStamp(Caller caller) => new(caller.User, Guid.NewGuid().ToString("N"), time.GetUtcNow(), Correlation.Current);
}
