using System.Diagnostics;

namespace BatchTracker.Application;

/// <summary>
/// One id per request or background action. It lives on an Activity because that flows through awaits by itself
/// and the logger puts its baggage on every line, so no call site passes it along.
/// </summary>
public static class Correlation
{
    public const string Header = "X-Correlation-Id";
    public const string Key = "correlationId";
    private const string FarmKey = "farm";
    private const string BatchKey = "batchId";
    private const string StepKey = "step";
    private const int MaxLength = 64;

    public static string? Current => Activity.Current?.GetBaggageItem(Key);

    public static string New() => Guid.NewGuid().ToString("N");

    public static bool IsValid(string? id) =>
        id is { Length: > 0 and <= MaxLength } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>Holds the id until disposed, then gives back the one before it.</summary>
    public static IDisposable Use(string id) => new Activity(Key).SetBaggage(Key, id).Start();

    /// <summary>The farm of the batch the action is working on, if any.</summary>
    public static string? Farm => Activity.Current?.GetBaggageItem(FarmKey);

    /// <summary>The batch the action is working on, if any.</summary>
    public static string? BatchId => Activity.Current?.GetBaggageItem(BatchKey);

    /// <summary>The step the action is working on, if any.</summary>
    public static string? Step => Activity.Current?.GetBaggageItem(StepKey);

    /// <summary>
    /// Names the batch for every log line of this action. Only for ids read from a stored batch or an internal event,
    /// because baggage goes out on each call the action makes and caller text must not.
    /// </summary>
    public static void About(string farm, string batch, string? step = null) =>
        Activity.Current?.SetBaggage(FarmKey, farm).SetBaggage(BatchKey, batch).SetBaggage(StepKey, step);
}
