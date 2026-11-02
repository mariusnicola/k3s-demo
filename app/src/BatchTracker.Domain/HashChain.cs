using System.Security.Cryptography;
using System.Text;

namespace BatchTracker.Domain;

public static class HashChain
{
    /// <summary>Fills the record of the last step and ends the event that produced it.</summary>
    public static Batch Seal(Batch batch, string recordId)
    {
        var last = batch.Steps[^1];
        var sealedStep = last with
        {
            RecordId = recordId,
            PreviousHash = batch.Steps.Count > 1 ? batch.Steps[^2].Hash : ""
        };
        sealedStep = sealedStep with { Hash = Compute(batch, sealedStep) };
        return batch with { Steps = [.. batch.Steps.SkipLast(1), sealedStep], Pending = null };
    }

    public static bool Verify(Batch batch)
    {
        var steps = batch.Steps;
        if (steps.Count == 0 || steps[0].State != BatchState.Created || steps[^1].State != batch.State)
            return false;

        var previousHash = "";
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (i > 0 && !BatchMachine.IsStep(steps[i - 1].State, step.State)) return false;

            // Only the newest step can still wait for its record.
            if (step.Hash.Length == 0)
            {
                if (i != steps.Count - 1 || batch.Pending is null) return false;
                continue;
            }
            if (step.PreviousHash != previousHash || step.Hash != Compute(batch, step)) return false;
            previousHash = step.Hash;
        }
        return true;
    }

    private static string Compute(Batch batch, Step step)
    {
        var reason = step.State == BatchState.Rejected ? batch.Reason : "";
        var text = string.Join('\u001f',
            batch.Id, batch.FarmId, step.State, step.At.UtcDateTime.ToString("O"),
            step.By, step.TraceId, step.RecordId, step.PreviousHash, reason);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
