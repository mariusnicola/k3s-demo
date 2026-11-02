using System.Security.Cryptography;
using System.Text;
using BatchTracker.Domain;

namespace BatchTracker.Application;

/// <summary>A step that passed every check needing no store read.</summary>
public sealed record MoveStep(string FarmId, string Id, string IfMatch, Func<Batch, Stamp, Move> Apply);

/// <summary>What a write must pass, so a single route and a bundle item cannot differ.</summary>
public static class BatchChecks
{
    public const int MaxProductLength = 200;
    public const int MaxReasonLength = 1000;
    public const int MaxKeyLength = 200;
    public const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>Null when the create may go ahead.</summary>
    public static Outcome<Versioned>? Create(Caller caller, string? farmId, string? product, string? key)
    {
        if (string.IsNullOrWhiteSpace(farmId) || string.IsNullOrWhiteSpace(product)) return Bad("farmId and product are required.");
        if (product.Length > MaxProductLength) return Bad($"product is limited to {MaxProductLength} characters.");
        if (key?.Length > MaxKeyLength) return Bad($"{IdempotencyHeader} is limited to {MaxKeyLength} characters.");
        if (!caller.HasFarm(farmId)) return NoFarm(farmId);
        return DemoUsers.KnownFarms.Contains(farmId) ? null : Bad($"Unknown farm {farmId}.");
    }

    /// <summary>The id comes from the key, so the store refuses a second write and no second event can exist.</summary>
    public static string IdOf(string user, string? key) => string.IsNullOrWhiteSpace(key)
        ? Guid.NewGuid().ToString()
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{user}\n{key}")))[..32];

    /// <summary>What a repeated key answers: the batch held, or a refusal when the body was another.</summary>
    public static Outcome<Versioned> Repeated(Versioned? held, string product) => held?.Batch.Product == product
        ? Outcome<Versioned>.Ok(held)
        : Outcome<Versioned>.Fail(Failure.KeyReused, $"This {IdempotencyHeader} was used with a different body.");

    public static Outcome<MoveStep> Advance(Caller caller, string farmId, string id, string? to, string? ifMatch)
    {
        if (!caller.HasFarm(farmId)) return NoFarm(farmId).As<MoveStep>();
        if (!TryParseState(to, out var target)) return Bad($"Unknown state {to}.").As<MoveStep>();
        return Step(farmId, id, ifMatch, (batch, stamp) => BatchMachine.Advance(batch, target, stamp));
    }

    public static Outcome<MoveStep> Inspect(Caller caller, string farmId, string id, bool? passed, string? reason, string? ifMatch)
    {
        if (!caller.HasFarm(farmId)) return NoFarm(farmId).As<MoveStep>();
        if (passed is null) return Bad("passed is required.").As<MoveStep>();
        if (reason?.Length > MaxReasonLength) return Bad($"reason is limited to {MaxReasonLength} characters.").As<MoveStep>();
        if (passed == false && string.IsNullOrWhiteSpace(reason)) return Bad("A failed inspection needs a reason.").As<MoveStep>();
        return Step(farmId, id, ifMatch, (batch, stamp) => BatchMachine.Inspect(batch, passed.Value, reason, stamp));
    }

    /// <summary>The batch the step leaves, or why the row refuses it.</summary>
    public static Outcome<Batch> Apply(Versioned? row, MoveStep step, Stamp stamp)
    {
        if (row is null) return Missing().As<Batch>();
        if (row.ETag != step.IfMatch) return Stale().As<Batch>();
        var result = step.Apply(row.Batch, stamp);
        if (result.Batch is not null) return Outcome<Batch>.Ok(result.Batch);
        return Outcome<Batch>.Fail(result.Refusal == Refusal.ReasonRequired ? Failure.Validation : Failure.Conflict, result.Message!);
    }

    /// <summary>Only a real id reaches the store: anything else is a batch that cannot exist.</summary>
    public static bool IsBatchId(string id) => Guid.TryParse(id, out _);

    public static bool TryParseState(string? text, out BatchState state)
    {
        state = default;
        return Enum.GetNames<BatchState>().Contains(text) && Enum.TryParse(text, out state);
    }

    public static Outcome<Versioned> Bad(string detail) => Outcome<Versioned>.Fail(Failure.Validation, detail);
    public static Outcome<Versioned> Missing() => Outcome<Versioned>.Fail(Failure.NotFound, "Batch not found.");
    public static Outcome<Versioned> Stale() => Outcome<Versioned>.Fail(Failure.Stale, "Batch changed, read it again.");
    public static Outcome<Versioned> NoFarm(string farmId) => Outcome<Versioned>.Fail(Failure.Forbidden, $"No access to farm {farmId}.");

    private static Outcome<MoveStep> Step(string farmId, string id, string? ifMatch, Func<Batch, Stamp, Move> apply) =>
        string.IsNullOrWhiteSpace(ifMatch)
            ? Outcome<MoveStep>.Fail(Failure.MissingIfMatch, "If-Match header is required.")
            : Outcome<MoveStep>.Ok(new MoveStep(farmId, id, ifMatch, apply));
}
