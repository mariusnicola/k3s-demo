using BatchTracker.Domain;

namespace BatchTracker.Application.Dispatching;

/// <summary>Puts the events the store holds on the queue.</summary>
public sealed class Outbox(IBatchStore store, IHopReporter hops, TimeProvider time, DispatcherOptions options)
{
    private DateTimeOffset? nextStaleRead;

    /// <summary>Rows the last sweep moved, so the caller can tell an idle store from a busy one.</summary>
    public int Moved { get; private set; }

    /// <summary>
    /// Sends what is due and what went stale, as one write and one bus message: a pending event blocks its batch, so a batch
    /// has one event in flight and its order holds. Nothing promises an order between batches. A full list waits for the
    /// next round like any other, because the rounds are what hold the bus to one message per interval.
    /// </summary>
    public async Task SweepAsync(IEventQueue queue, CancellationToken token)
    {
        var due = store.FindByPendingStatusAsync(PendingStatus.Pending, options.SweepSize, token);
        var stale = ReadStaleAsync(token);
        var lists = await Task.WhenAll(due, stale);
        var rows = lists.SelectMany(list => list).ToList();

        // Marks before sending: a crash in between is healed by the stale sweep, the reverse order could lose the event.
        var marked = rows.Count == 0 ? [] : await store.MarkSentAsync(rows, time.GetUtcNow(), token);
        Moved = marked.Count;
        if (marked.Count > 0) await SendAsync(queue, marked, token);
    }

    /// <summary>A row goes stale after minutes, so reading every sweep would cost a query per farm and find nothing sooner.</summary>
    private Task<IReadOnlyList<Versioned>> ReadStaleAsync(CancellationToken token)
    {
        var now = time.GetUtcNow();
        if (now < nextStaleRead) return Task.FromResult<IReadOnlyList<Versioned>>([]);
        nextStaleRead = now + options.StaleSweepEvery;
        return store.FindSentBeforeAsync(now - options.ResendAfter, options.SweepSize, token);
    }

    private async Task SendAsync(IEventQueue queue, IReadOnlyList<Versioned> rows, CancellationToken token)
    {
        var events = rows.Select(EventOf).ToList();
        Report(events, HopNames.Dispatcher);
        try
        {
            await queue.SendAsync(events, token);
        }
        catch when (!token.IsCancellationRequested)
        {
            // The caller logs the error, past every event's own action, so the round has to name one.
            Correlation.About(events[0].FarmId, events[0].BatchId, events[0].State);
            throw;
        }
        Report(events, HopNames.Bus);
    }

    private static BatchEvent EventOf(Versioned row)
    {
        var (batch, pending) = (row.Batch, row.Batch.Pending!);
        return new BatchEvent(pending.EventId, batch.Id, batch.FarmId, pending.TraceId, batch.State.ToString(), pending.CorrelationId ?? Correlation.New());
    }

    private void Report(IReadOnlyList<BatchEvent> events, string hop)
    {
        foreach (var e in events)
        {
            using var action = Correlation.Use(e.CorrelationId!);
            Correlation.About(e.FarmId, e.BatchId, e.State);
            hops.Report(new Hop(e.TraceId, e.BatchId, e.FarmId, hop, e.State, time.GetUtcNow(), true));
        }
    }
}
