namespace BatchTracker.Application.Dispatching;

public sealed class DispatcherRunner(
    ILeadership leadership, IEventQueueFactory queues,
    IWorkerClient worker,
    Outbox outbox,
    DeadLetters deadLetters,
    DispatcherStatus status, IHopReporter hops, TimeProvider time,
    DispatcherOptions options,
    Action<Exception, string> logError)
{
    private readonly WorkerOutage outage = new(worker, options);
    private readonly SweepPace pace = new(options);

    public async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            if (await NextTermAsync(stopping) is not { } term) return;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, term);
            using var lost = linked.Token.Register(() => status.Connected = false);
            status.TermStarted();
            try
            {
                await RunTermAsync(linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
            }
            finally
            {
                status.TermEnded();
                outage.Forget();
            }
        }
    }

    /// <summary>Null when the service stops before leadership comes.</summary>
    private async Task<CancellationToken?> NextTermAsync(CancellationToken stopping)
    {
        try
        {
            return await leadership.WaitForLeadershipAsync(stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task RunTermAsync(CancellationToken token)
    {
        await using var queue = await ConnectAsync(token);
        using var slots = new SemaphoreSlim(options.DeliveryConcurrency);
        status.Connected = true;
        try
        {
            await Task.WhenAll(
                RepeatAsync(() => SweepAsync(queue, token), () => pace.After(outbox.Moved), "sweep", token),
                RepeatAsync(() => ReceiveAsync(queue, slots, token), () => TimeSpan.Zero, "receive", token),
                RepeatAsync(() => DrainDeadLettersAsync(queue, token), () => TimeSpan.Zero, "dead letters", token));
        }
        finally
        {
            // Holding every slot means no delivery still uses the queue this term is about to close.
            for (var i = 0; i < options.DeliveryConcurrency; i++) await slots.WaitAsync(CancellationToken.None);
        }
    }

    private async Task<IEventQueue> ConnectAsync(CancellationToken token)
    {
        while (true)
        {
            try
            {
                return await queues.ConnectAsync(token);
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                logError(e, "connect");
                await Task.Delay(options.ErrorDelay, token);
            }
        }
    }

    private async Task RepeatAsync(Func<Task> step, Func<TimeSpan> wait, string name, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var action = Correlation.Use(Correlation.New());
            try
            {
                await step();
                if (wait() is var interval && interval > TimeSpan.Zero) await Task.Delay(interval, token);
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                logError(e, name);
                await Task.Delay(options.ErrorDelay, token);
            }
        }
    }

    private async Task SweepAsync(IEventQueue queue, CancellationToken token)
    {
        await outage.WaitUntilOverAsync(token);
        await outbox.SweepAsync(queue, token);
        status.Swept();
    }

    /// <summary>Takes one pack and gives each event its own slot, so a slow delivery holds its slot and nothing else. The next pack waits for the last slot here.</summary>
    private async Task ReceiveAsync(IEventQueue queue, SemaphoreSlim slots, CancellationToken token)
    {
        if (outage.IsDown) await outage.ProbeUntilReadyAsync(token);
        foreach (var message in await TakeAsync(queue, token))
        {
            await slots.WaitAsync(token);
            _ = DeliverAsync(message, slots, token);
        }
    }

    private async Task<IReadOnlyList<IDelivery>> TakeAsync(IEventQueue queue, CancellationToken token)
    {
        try
        {
            var messages = await queue.ReceiveAsync(options.ReceiveWait, token);
            status.Connected = true;
            return messages;
        }
        catch when (!token.IsCancellationRequested)
        {
            status.Connected = false;
            throw;
        }
    }

    /// <summary>Nothing awaits this: it logs its own failure, under the event's correlation id, and always frees its slot.</summary>
    private async Task DeliverAsync(IDelivery message, SemaphoreSlim slots, CancellationToken token)
    {
        using var action = Correlation.Use(message.Event.CorrelationId ?? Correlation.New());
        Correlation.About(message.Event.FarmId, message.Event.BatchId, message.Event.State);
        hops.Report(new Hop(message.Event.TraceId, message.Event.BatchId, message.Event.FarmId, HopNames.Deliver, message.Event.State, time.GetUtcNow(), true));
        try
        {
            // Received while an outage began: the Worker is known down, so it goes back unposted.
            var recorded = outage.IsDown
                ? await deadLetters.RequeueAsync(message.Event, token)
                : await RecordAsync(message.Event, await worker.PostEventAsync(message.Event, token), token);

            // Not recorded: no count down, so the pack's lock lapses and it comes back whole.
            if (recorded) await message.CompleteAsync(token);
        }
        catch (Exception e)
        {
            if (!token.IsCancellationRequested) logError(e, "receive");
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>True once the row says what became of the event.</summary>
    private async Task<bool> RecordAsync(BatchEvent message, WorkerResult result, CancellationToken token)
    {
        // Before the row write: its round trip would leave the sweep running during an outage.
        outage.Note(result);
        if (result == WorkerResult.Done) return true;
        if (result == WorkerResult.Refused) return await deadLetters.RefuseAsync(message, DispatcherOptions.RefusedText, token);

        logError(new InvalidOperationException($"Worker answered {result}."), "deliver");
        return await deadLetters.RequeueAsync(message, token);
    }

    private async Task DrainDeadLettersAsync(IEventQueue queue, CancellationToken token)
    {
        var letters = await queue.ReceiveDeadLettersAsync(options.DeadLetterRead, options.ReceiveWait, token);
        foreach (var letter in letters) await deadLetters.HandleAsync(letter, token);
    }
}
