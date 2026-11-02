namespace BatchTracker.Application;

public enum Failure { Validation, Forbidden, NotFound, Conflict, Stale, MissingIfMatch, KeyReused }

public sealed record Outcome<T>(T? Value, Failure? Failure, string? Detail)
{
    public static Outcome<T> Ok(T value) => new(value, null, null);
    public static Outcome<T> Fail(Failure failure, string detail) => new(default, failure, detail);

    /// <summary>The same failure as an outcome of another type.</summary>
    public Outcome<TOther> As<TOther>() => new(default, Failure, Detail);
}
