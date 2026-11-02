using System.Collections.Concurrent;
using System.Net.WebSockets;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.Line;

/// <summary>Lets test hosts find this assembly's entry point.</summary>
public sealed class LineMarker;

public sealed record RateRequest(double? Rate);

public sealed record LineStatus(
    bool Running, double Rate, long Started, long Closed, long Rejected, long Errors,
    IReadOnlyDictionary<string, long> ErrorsByReason, int InFlight, int Throttled, LineBundleReading Bundles);

public sealed record LineOptions
{
    public TimeSpan StopWait { get; init; } = TimeSpan.FromSeconds(15);
    public string MachineToken { get; init; } = "";
    public TimeSpan EventTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan PollDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaxPollDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan SocketRetry { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan BundleEvery { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Opens the socket with a bearer. Left null the Line has no socket and only reads.</summary>
    public Func<string, CancellationToken, Task<WebSocket>>? OpenSocket { get; init; }

    /// <summary>A number from 0 to 1 per wait. A test sets it, so a wait's length can be known.</summary>
    public Func<double> Roll { get; init; } = Random.Shared.NextDouble;

    public int MaxInFlight { get; init; } = 500;
}

public sealed class LineEngine(LineApi api, LineLog log, LineOptions options, TimeProvider time, ILogger<LineEngine> logger) : BackgroundService
{
    public const double DefaultRate = 1;
    public const double MaxRate = 35;
    private const int RejectEvery = 10;
    private const double MaxPause = 60;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(100);

    private readonly IReadOnlyList<string> farms = DemoUsers.KnownFarms;
    private readonly SemaphoreSlim slots = new(options.MaxInFlight);
    private readonly ConcurrentDictionary<long, Task> walking = new();
    private readonly CancellationTokenSource walks = new();
    private readonly LineBundleSender sender = new(api, options, time, logger);
    private Task sending = Task.CompletedTask;
    private volatile bool running;
    private double rate;
    private long nextNumber;

    public void Start(double newRate)
    {
        Volatile.Write(ref rate, newRate);
        running = true;
    }

    public void Stop() => running = false;

    /// <summary>The client whose bundle this engine sends. A service resolves its own, so a test calling steps takes this one.</summary>
    public LineApi Api => api;

    public LineBundleReading Bundles() => api.Figures.Reading();

    public LineStatus Status()
    {
        var counts = log.Counts();
        return new LineStatus(
            running, Volatile.Read(ref rate),
            counts.Started, counts.Closed, counts.Rejected, counts.Errors, counts.ErrorsByReason,
            options.MaxInFlight - slots.CurrentCount,
            api.Throttled,
            Bundles());
    }

    /// <summary>The base stop ends the loop, so no walk starts; those under way get the wait, then are cancelled. Sending runs on the walks' token, so they can finish.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        var finished = Task.WhenAll(walking.Values);
        try
        {
            await finished.WaitAsync(options.StopWait, cancellationToken);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            logger.LogWarning("Line stopped with {Count} batches mid walk", walking.Count);
        }
        await walks.CancelAsync();
        await finished.ContinueWith(_ => { }, TaskScheduler.Default);
        await sending.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        sending = sender.RunAsync(walks.Token);
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = Volatile.Read(ref rate);
            if (!running || current <= 0)
            {
                await Task.Delay(IdleDelay, stoppingToken);
                continue;
            }
            // Waiting here is the back pressure: at the cap, batches in flight hold the rate down.
            await slots.WaitAsync(stoppingToken);
            if (!running)
            {
                slots.Release();
                continue;
            }
            var number = Interlocked.Increment(ref nextNumber);
            walking[number] = RunHoldingSlotAsync(number, walks.Token);
            // Task.Delay refuses anything past 49 days, and a very low rate would stop the line host.
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 / current, MaxPause)), stoppingToken);
        }
    }

    private async Task RunHoldingSlotAsync(long number, CancellationToken cancel)
    {
        try
        {
            await RunBatchAsync(number, cancel);
        }
        finally
        {
            slots.Release();
            walking.TryRemove(number, out _);
        }
    }

    public async Task RunBatchAsync
(long number, CancellationToken cancel)
    {
        var attempt = new LineAttempt(number, farms[(int)(number % farms.Count)], time);
        using var action = Correlation.Use(attempt.CorrelationId);
        log.Add(Info(attempt, LineReason.Started, $"Batch {number} started on {attempt.Farm}.", 0));
        try
        {
            var batch = await api.CreateAsync(attempt.Farm, $"Line batch {number}", attempt, cancel);
            await AdvanceAsync(batch, BatchState.Dose1, attempt, cancel);
            await AdvanceAsync(batch, BatchState.Dose2, attempt, cancel);

            attempt.Begin(LineStep.Inspect);
            if (number % RejectEvery == 0)
            {
                await api.InspectAsync(batch, false, "Line inspection failure", attempt, cancel);
                log.Add(Info(attempt, LineReason.Rejected, $"Batch {number} failed inspection on purpose, as one in ten does.", attempt.WalkMs));
                return;
            }
            await api.InspectAsync(batch, true, null, attempt, cancel);
            await AdvanceAsync(batch, BatchState.Shipped, attempt, cancel);
            await AdvanceAsync(batch, BatchState.Closed, attempt, cancel);
            log.Add(Info(attempt, LineReason.Closed, $"Batch {number} closed.", attempt.WalkMs));
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            log.Add(LineFailures.ToEntry(e, attempt, time.GetUtcNow()));
            logger.LogWarning("Line batch {Number} failed at {Step}: {Type}", number, attempt.Step, e.GetType().Name);
        }
    }

    private Task AdvanceAsync(Batch batch, BatchState to, LineAttempt attempt, CancellationToken cancel)
    {
        attempt.Begin(to.ToString());
        return api.AdvanceAsync(batch, to, attempt, cancel);
    }

    private LineEntry Info(LineAttempt attempt, string reason, string message, long waitedMs) => new(
        time.GetUtcNow(), LineLevel.Info, reason, message, attempt.Number, attempt.BatchId, attempt.Farm, attempt.Step,
        waitedMs, attempt.LastStatus, null, null, attempt.TraceId, attempt.CorrelationId);
}
