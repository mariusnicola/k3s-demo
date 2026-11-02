namespace BatchTracker.Domain;

public static class Dose2Rule
{
    public const string FlagType = "Dose2Overdue";
    public const int DefaultWindowDays = 21;

    public static bool NeedsFlag(Batch batch, DateTimeOffset now, int windowDays)
    {
        if (batch.State != BatchState.Dose1 || batch.Flags.Any(f => f.Type == FlagType)) return false;
        var dose1 = batch.Steps.Last(s => s.State == BatchState.Dose1);
        return now > dose1.At.AddDays(windowDays);
    }

    public static Batch Flag(Batch batch, DateTimeOffset now) =>
        batch with { Flags = [.. batch.Flags, new Flag(FlagType, now)] };
}
