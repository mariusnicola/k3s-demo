using BatchTracker.Domain;

namespace BatchTracker.Application;

public sealed record ComplianceOptions(int Dose2WindowDays);

public sealed record ComplianceResult(int Flagged);

public sealed class ComplianceRun(IBatchStore store, ComplianceOptions options, TimeProvider time)
{
    public async Task<ComplianceResult> RunAsync(CancellationToken token)
    {
        var now = time.GetUtcNow();
        var flagged = 0;
        foreach (var row in await store.FindInStateAsync(BatchState.Dose1, token))
        {
            if (!Dose2Rule.NeedsFlag(row.Batch, now, options.Dose2WindowDays)) continue;

            // A lost write means the batch just changed; the next run sees it again.
            if (await store.ReplaceAsync(Dose2Rule.Flag(row.Batch, now), row.ETag, token) is not null) flagged++;
        }
        return new ComplianceResult(flagged);
    }
}
