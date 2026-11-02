using System.Net;
using System.Text.Json;
using BatchTracker.Domain;

namespace BatchTracker.Line;

/// <summary>What the Line makes of an API answer, whichever route it came from.</summary>
public static class LineReplies
{
    /// <summary>The batch an item came to, or the refusal its walk logs.</summary>
    public static Batch Read(LineAnswer answer, LineAttempt attempt)
    {
        attempt.LastStatus = answer.Status;
        if (answer.Batch is null) throw Refused(answer.Status, answer.Detail ?? "no detail given", answer.Attempts, attempt);
        attempt.See(answer.Batch);
        return answer.Batch;
    }

    public static LineFailure Refused(int status, string detail, int tries, LineAttempt attempt) => new(
        LineReason.HttpRefused,
        $"The API refused step {attempt.Step} of batch {attempt.Number} with HTTP {status} after {tries} attempts: {detail}",
        new HttpRequestException(detail, null, (HttpStatusCode)status),
        status);

    /// <summary>An ingress or proxy error is not RFC 7807, so the status text stands in.</summary>
    public static async Task<string> ProblemDetailAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            if (problem.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text) return text;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
        }
        return response.ReasonPhrase ?? "no detail given";
    }
}
