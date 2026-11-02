using System.Net.Sockets;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.Line;

/// <summary>What one batch's walk has reached, so a failure can say where it happened.</summary>
public sealed class LineAttempt(long number, string farm, TimeProvider time)
{
    private readonly long walkStart = time.GetTimestamp();
    private long stepStart = time.GetTimestamp();

    public long Number { get; } = number;
    public string Farm { get; } = farm;
    public string Step { get; private set; } = LineStep.Create;
    public string? BatchId { get; private set; }
    public string? TraceId { get; private set; }
    public string CorrelationId { get; } = Correlation.New();
    public int? LastStatus { get; set; }

    public long StepMs => (long)time.GetElapsedTime(stepStart).TotalMilliseconds;
    public long WalkMs => (long)time.GetElapsedTime(walkStart).TotalMilliseconds;

    public void Begin(string step)
    {
        Step = step;
        stepStart = time.GetTimestamp();
    }

    public void See(Batch batch)
    {
        BatchId = batch.Id;
        TraceId = batch.Pending?.TraceId ?? batch.Steps.LastOrDefault()?.TraceId ?? TraceId;
    }
}

public static class LineStep
{
    public const string Create = "Create";
    public const string Inspect = "Inspect";
}

public sealed class LineFailure(string reason, string message, Exception detail, int? status = null) : Exception(message, detail)
{
    public string Reason { get; } = reason;
    public int? Status { get; } = status;
    public Exception Detail { get; } = detail;
}

public static class LineFailures
{
    public static LineEntry ToEntry(Exception failure, LineAttempt attempt, DateTimeOffset at)
    {
        var (reason, message, detail) = Classify(failure, attempt);
        return new LineEntry(
            at, LineLevel.Error, reason, Secrets.Redact(message),
            attempt.Number, attempt.BatchId, attempt.Farm, attempt.Step, attempt.StepMs,
            attempt.LastStatus, detail.GetType().Name, Secrets.Redact(detail.Message), attempt.TraceId, attempt.CorrelationId);
    }

    public static string EventTimeoutSentence(LineAttempt attempt, TimeSpan waited, string? status)
    {
        var start = $"The Line stopped waiting after {waited.TotalSeconds:0.#} s for the previous event of batch {attempt.Number} " +
            $"before step {attempt.Step}. Last seen event status: {status ?? "none"}.";
        return status == PendingStatus.Dead
            ? $"{start} The batch is blocked until an admin replays it."
            : $"{start} The batch is not failed and its event may finish later.";
    }

    private static (string Reason, string Message, Exception Detail) Classify(Exception failure, LineAttempt attempt)
    {
        var where = $"step {attempt.Step} of batch {attempt.Number}";
        return failure switch
        {
            LineFailure known => (known.Reason, known.Message, known.Detail),
            _ when IsNetwork(failure) => (
                LineReason.NetworkFailure,
                $"The Line could not reach the API during {where}, so the request may or may not have arrived.",
                failure),
            _ => (LineReason.Unknown, $"Unexpected failure during {where}.", failure)
        };
    }

    /// <summary>HttpClient reports its own timeout as a cancellation; the caller filters out real shutdowns.</summary>
    public static bool IsNetwork(Exception failure) =>
        failure is HttpRequestException or SocketException or IOException or OperationCanceledException;
}
