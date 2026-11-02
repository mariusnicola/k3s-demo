using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.Line;

/// <summary>Talks to the API with its own credential, so the line exercises every check a person would.</summary>
public sealed class LineApi(HttpClient http, LineOptions options, TimeProvider time, LineSettled settled)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim gate = new(1, 1);
    private string? token;
    private DateTimeOffset tokenAt;

    public int Throttled => Volatile.Read(ref throttled);

    private int throttled;

    /// <summary>Half either side, not less only: the average stays, so spreading the reads adds none to the store.</summary>
    public static TimeSpan Spread(TimeSpan wait, double roll) => wait * (0.5 + roll);

    public LineBundle Bundle { get; } = new();

    public LineBundleFigures Figures { get; } = new(time);

    public async Task<Batch> CreateAsync(string farmId, string product, LineAttempt attempt, CancellationToken cancel)
    {
        var item = new BundleItem(BundleKinds.Create, attempt.CorrelationId, farmId, Product: product, Key: Guid.NewGuid().ToString());
        return LineReplies.Read(await SendAsync(item, attempt, cancel), attempt);
    }

    public async Task AdvanceAsync(Batch batch, BatchState to, LineAttempt attempt, CancellationToken cancel) =>
        await MoveAsync(batch, new BundleItem(BundleKinds.Advance, attempt.CorrelationId, batch.FarmId, batch.Id, To: to.ToString()), attempt, cancel);

    public async Task InspectAsync(Batch batch, bool passed, string? reason, LineAttempt attempt, CancellationToken cancel) =>
        await MoveAsync(batch, new BundleItem(BundleKinds.Inspect, attempt.CorrelationId, batch.FarmId, batch.Id, Passed: passed, Reason: reason), attempt, cancel);

    private async Task MoveAsync(Batch batch, BundleItem item, LineAttempt attempt, CancellationToken cancel)
    {
        var (etag, state) = await WaitUntilSettledAsync(batch, attempt, cancel);
        var answer = await SendAsync(item with { IfMatch = etag }, attempt, cancel);
        if (await EarlierAttemptAppliedAsync(answer, batch, state, attempt, cancel)) return;
        LineReplies.Read(answer, attempt);
    }

    /// <summary>The token first: it is cached, and a refused sign-in then names this walk's step and not the POST's.</summary>
    private async Task<LineAnswer> SendAsync(BundleItem item, LineAttempt attempt, CancellationToken cancel)
    {
        await TokenAsync(attempt, cancel);
        return await Bundle.AddAsync(item, cancel);
    }

    /// <summary>Safe to repeat: each item's key or If-Match refuses a second write. A refused POST gives every item its status.</summary>
    public async Task<LineBundleReply> PostBundleAsync(IReadOnlyList<BundleItem> items, CancellationToken cancel)
    {
        using var request = await NewRequestAsync(HttpMethod.Post, "batches/bundle", null, new LineAttempt(0, "", time), cancel);
        request.SafeToRepeat().Content = JsonContent.Create(new { items }, options: JsonDefaults.Options);
        using var response = await CountedAsync(request, cancel);
        var tries = response.RequestMessage?.AttemptsMade() ?? 1;
        if (!response.IsSuccessStatusCode)
        {
            var refused = new LineAnswer((int)response.StatusCode, null, null, await LineReplies.ProblemDetailAsync(response, cancel), tries);
            return new LineBundleReply(items.Select(_ => refused).ToList(), true);
        }
        var body = (await response.Content.ReadFromJsonAsync<BundleBody>(JsonDefaults.Options, cancel))!;
        if (body.Results?.Count != items.Count) throw new InvalidOperationException($"The API answered {body.Results?.Count ?? 0} results for {items.Count} items.");
        return new LineBundleReply(body.Results.Select(r => new LineAnswer(r.Status, r.Etag, r.Batch, r.Detail, tries)).ToList(), false);
    }

    /// <summary>
    /// A 412 after a retry only proves the ETag moved, so the batch must also have left the state this step started from.
    /// </summary>
    private async Task<bool> EarlierAttemptAppliedAsync(
        LineAnswer answer, Batch batch, BatchState before, LineAttempt attempt, CancellationToken cancel)
    {
        if (answer.Status != (int)HttpStatusCode.PreconditionFailed || answer.Attempts <= 1) return false;
        using var request = await NewRequestAsync(HttpMethod.Get, $"batches/{batch.FarmId}/{batch.Id}", null, attempt, cancel);
        using var current = await CountedAsync(request, cancel);
        return (await ReadBatchAsync(current, attempt, cancel)).State != before;
    }


    private async Task<HttpResponseMessage> CountedAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        try
        {
            return await http.SendAsync(request, cancel);
        }
        finally
        {
            Interlocked.Add(ref throttled, request.TimesThrottled());
        }
    }

    /// <summary>
    /// The Worker changes the document after each step, so the ETag is only good once its event is done. Each read
    /// loads the store the event is waiting on, so with the socket up a frame ends the wait and a read is only the
    /// fallback for a lost frame. With it down the wait between reads doubles up to its limit, each wait spread on
    /// its own so walks that stepped together do not read together.
    /// </summary>
    private async Task<(string ETag, BatchState State)> WaitUntilSettledAsync(Batch batch, LineAttempt attempt, CancellationToken cancel)
    {
        using var watch = settled.Watch(batch.Id);
        var start = time.GetTimestamp();
        var delay = options.PollDelay;
        while (true)
        {
            using var request = await NewRequestAsync(HttpMethod.Get, $"batches/{batch.FarmId}/{batch.Id}", null, attempt, cancel);
            using var response = await CountedAsync(request, cancel);
            var current = await ReadBatchAsync(response, attempt, cancel);
            if (current.Pending is null) return (response.Headers.ETag!.ToString(), current.State);

            var waited = time.GetElapsedTime(start);
            if (waited > options.EventTimeout)
            {
                var status = current.Pending.Status;
                throw new LineFailure(
                    LineReason.EventTimeout,
                    LineFailures.EventTimeoutSentence(attempt, waited, status),
                    new TimeoutException($"Event {current.Pending.EventId} still {status} after {waited.TotalSeconds:0.#} s."));
            }
            var wait = Spread(settled.Connected ? options.MaxPollDelay : delay, options.Roll());
            if (await watch.AfterAsync(current.Version, wait, cancel) is { } done) return (done.ETag, done.State);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, options.MaxPollDelay.Ticks));
        }
    }

    private static async Task<Batch> ReadBatchAsync(HttpResponseMessage response, LineAttempt attempt, CancellationToken cancel)
    {
        attempt.LastStatus = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
            throw LineReplies.Refused(attempt.LastStatus.Value, await LineReplies.ProblemDetailAsync(response, cancel), response.RequestMessage?.AttemptsMade() ?? 1, attempt);
        var batch = (await response.Content.ReadFromJsonAsync<Batch>(JsonDefaults.Options, cancel))!;
        attempt.See(batch);
        return batch;
    }

    private async Task<HttpRequestMessage> NewRequestAsync(
        HttpMethod method, string path, string? etag, LineAttempt attempt, CancellationToken cancel)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(attempt, cancel));
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return request;
    }

    /// <summary>The token the socket opens with. It runs outside any walk, so the attempt is a blank one.</summary>
    public Task<string> BearerAsync(CancellationToken cancel) => TokenAsync(new LineAttempt(0, "", time), cancel);

    private async Task<string> TokenAsync(LineAttempt attempt, CancellationToken cancel)
    {
        await gate.WaitAsync(cancel);
        try
        {
            if (token is not null && DateTimeOffset.UtcNow - tokenAt < TokenLifetime) return token;
            using var login = new HttpRequestMessage(HttpMethod.Post, "auth/machine").SafeToRepeat();
            login.Headers.Add(MachineCredential.Header, options.MachineToken);
            using var response = await CountedAsync(login, cancel);
            attempt.LastStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(await LineReplies.ProblemDetailAsync(response, cancel), null, response.StatusCode);
            var reply = (await response.Content.ReadFromJsonAsync<LoginReply>(JsonDefaults.Options, cancel))!;
            tokenAt = DateTimeOffset.UtcNow;
            return token = reply.Token;
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            throw new LineFailure(
                LineReason.LoginFailure,
                $"The Line could not sign in, so step {attempt.Step} of batch {attempt.Number} did not run.",
                e,
                attempt.LastStatus);
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record LoginReply(string Token);
    private sealed record BundleBody(IReadOnlyList<BundleResultBody>? Results);
    private sealed record BundleResultBody(int Status, string? Etag, Batch? Batch, string? Detail);
}
