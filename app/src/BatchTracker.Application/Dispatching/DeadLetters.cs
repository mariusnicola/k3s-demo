using BatchTracker.Domain;

namespace BatchTracker.Application.Dispatching;

/// <summary>A pack cannot dead-letter one event, so what becomes of a failed event is written on its row.</summary>
public sealed class DeadLetters(IBatchStore store, IHopReporter hops, TimeProvider time, IBatchCounters counters)
{
    /// <summary>A pack the bus gave up on: each event goes back, and a lost write brings the letter round again.</summary>
    public async Task HandleAsync(IDelivery letter, CancellationToken token)
    {
        var message = letter.Event;
        using var action = Correlation.Use(message.CorrelationId ?? Correlation.New());
        Correlation.About(message.FarmId, message.BatchId, message.State);
        if (await RequeueAsync(message, token)) await letter.CompleteAsync(token);
    }

    /// <summary>The Worker refused. True once nothing is left to do. A dead row counts nothing, so a repeat counts once.</summary>
    public async Task<bool> RefuseAsync(BatchEvent message, string reason, CancellationToken token)
    {
        if (await RowOfAsync(message, token) is not { } row || row.Batch.IsBlocked()) return true;
        if (!await WriteAsync(row, message, PendingStatus.Dead, reason, token)) return false;
        counters.Count(MetricNames.EventsDeadLettered, message.FarmId);
        return true;
    }

    /// <summary>Back to pending for the next sweep. Only from sent, so a dead row stays dead. True once nothing is left to do.</summary>
    public async Task<bool> RequeueAsync(BatchEvent message, CancellationToken token)
    {
        if (await RowOfAsync(message, token) is not { } row || row.Batch.Pending!.Status != PendingStatus.Sent) return true;
        return await WriteAsync(row, message, PendingStatus.Pending, null, token);
    }

    /// <summary>Null when the row has moved past this event.</summary>
    private async Task<Versioned?> RowOfAsync(BatchEvent message, CancellationToken token)
    {
        var row = await store.GetAsync(message.FarmId, message.BatchId, token);
        return row?.Batch.Pending?.EventId == message.EventId ? row : null;
    }

    private async Task<bool> WriteAsync(Versioned row, BatchEvent message, string status, string? reason, CancellationToken token)
    {
        var pending = row.Batch.Pending! with { Status = status, SentAt = null, Error = reason };
        if (await store.ReplaceAsync(row.Batch with { Pending = pending }, row.ETag, token) is null) return false;
        hops.Report(new Hop(message.TraceId, message.BatchId, message.FarmId, HopNames.Dispatcher, message.State,
            time.GetUtcNow(), status != PendingStatus.Dead));
        return true;
    }
}
