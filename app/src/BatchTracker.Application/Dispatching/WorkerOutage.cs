namespace BatchTracker.Application.Dispatching;

/// <summary>Stops receiving and sweeping from the last of several failures in a row until the Worker answers ready again.</summary>
public sealed class WorkerOutage(IWorkerClient worker, DispatcherOptions options)
{
    private TaskCompletionSource? down;
    private int failures;

    public bool IsDown => Volatile.Read(ref down) is not null;

    /// <summary>Any answer that came from a Worker shows one is there, so only failures with no answer between them add up.</summary>
    public void Note(WorkerResult result)
    {
        if (result != WorkerResult.Unavailable) Volatile.Write(ref failures, 0);
        else if (Interlocked.Increment(ref failures) >= options.WorkerFailuresToStop) Begin();
    }

    private void Begin() => Interlocked.CompareExchange(ref down, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);

    /// <summary>A new term starts with no outage: failures in that term find out again.</summary>
    public void Forget()
    {
        Volatile.Write(ref failures, 0);
        Volatile.Write(ref down, null);
    }

    /// <summary>For the sweep: returns at once unless an outage is on.</summary>
    public async Task WaitUntilOverAsync(CancellationToken token)
    {
        if (Volatile.Read(ref down) is { } current) await current.Task.WaitAsync(token);
    }

    /// <summary>For the one loop that probes the Worker, called only during an outage.</summary>
    public async Task ProbeUntilReadyAsync(CancellationToken token)
    {
        var current = down!;
        try
        {
            do await Task.Delay(options.WorkerProbeInterval, token);
            while (!await worker.ReadyAsync(token));
        }
        finally
        {
            Volatile.Write(ref failures, 0);
            Volatile.Write(ref down, null);
            current.TrySetResult();
        }
    }
}
