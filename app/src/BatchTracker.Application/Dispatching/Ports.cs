namespace BatchTracker.Application.Dispatching;

public enum WorkerResult { Done, Refused, Contended, Unavailable }

public interface IWorkerClient
{
    Task<WorkerResult> PostEventAsync(BatchEvent message, CancellationToken token);
    Task<bool> ReadyAsync(CancellationToken token);
    Task<ComplianceResult> RunNightlyAsync(CancellationToken token);
}

public interface IDelivery
{
    BatchEvent Event { get; }

    /// <summary>Says this event has its outcome. The bus message completes with the last event in it.</summary>
    Task CompleteAsync(CancellationToken token);
}

public interface IEventQueue : IAsyncDisposable
{
    /// <summary>The whole list as one bus message.</summary>
    Task SendAsync(IReadOnlyList<BatchEvent> messages, CancellationToken token);

    /// <summary>The events of at most one bus message.</summary>
    Task<IReadOnlyList<IDelivery>> ReceiveAsync(TimeSpan wait, CancellationToken token);

    Task<IReadOnlyList<IDelivery>> ReceiveDeadLettersAsync(int max, TimeSpan wait, CancellationToken token);
}

public interface IEventQueueFactory
{
    /// <summary>Returns only once the bus has answered.</summary>
    Task<IEventQueue> ConnectAsync(CancellationToken token);
}

public interface ILeadership
{
    /// <summary>Completes when this pod leads; the returned token cancels the moment leadership is lost.</summary>
    Task<CancellationToken> WaitForLeadershipAsync(CancellationToken token);
}
