using System.Diagnostics;
using BatchTracker.Domain;

namespace BatchTracker.Application;

public enum RecordResult { Done, Refused, Contended }

public sealed class EventRecorder(IBatchStore store, IHopReporter hops, TimeProvider time, IBatchCounters counters, StoreClock clock)
{
    private const int Attempts = 2;

    public async Task<RecordResult> HandleAsync(BatchEvent message, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await RecordAsync(message, token);
        }
        finally
        {
            counters.Time(Stages.Worker, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private async Task<RecordResult> RecordAsync(BatchEvent message, CancellationToken token)
    {
        Correlation.About(message.FarmId, message.BatchId, message.State);
        Report(message, HopNames.Worker, ok: true);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var row = await store.GetAsync(message.FarmId, message.BatchId, token);
            if (row?.Batch.Pending?.EventId != message.EventId) return RecordResult.Done;
            if (attempt == 0) await TimeQueueAsync(row.Batch.Pending, token);
            if (row.Batch.FailNext)
            {
                Report(message, HopNames.Worker, ok: false);
                return RecordResult.Refused;
            }

            var saved = await store.ReplaceAsync(HashChain.Seal(row.Batch, Guid.NewGuid().ToString()), row.ETag, token);
            if (saved is not null)
            {
                counters.Count(MetricNames.EventsPassed, message.FarmId);
                Report(message, HopNames.Record, ok: true);
                return RecordResult.Done;
            }
        }
        return RecordResult.Contended;
    }

    /// <summary>Both ends are the store's clock. Each carries up to half a round trip of error, so a very short wait can read below zero.</summary>
    private async ValueTask TimeQueueAsync(PendingEvent pending, CancellationToken token)
    {
        if (pending.StoredAt is { } stored && await clock.NowAsync(token) is { } now)
            counters.Time(Stages.Queue, Math.Max(0, (now - stored).TotalSeconds));
    }

    private void Report(BatchEvent message, string hop, bool ok) => hops.Report(new Hop(
        message.TraceId, message.BatchId, message.FarmId, hop, message.State, time.GetUtcNow(), ok));
}
