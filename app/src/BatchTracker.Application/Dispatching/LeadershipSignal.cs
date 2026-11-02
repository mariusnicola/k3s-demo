namespace BatchTracker.Application.Dispatching;

public sealed class LeadershipSignal : ILeadership
{
    private readonly object sync = new();
    private TaskCompletionSource<CancellationToken> gained = NewGained();
    private CancellationTokenSource? term;

    public Task<CancellationToken> WaitForLeadershipAsync(CancellationToken token)
    {
        lock (sync) return gained.Task.WaitAsync(token);
    }

    public void Gain()
    {
        lock (sync)
        {
            term ??= new CancellationTokenSource();
            gained.TrySetResult(term.Token);
        }
    }

    public void Lose()
    {
        CancellationTokenSource? ended;
        lock (sync)
        {
            ended = term;
            term = null;
            // A lease error also lands here on a pod that never led. Its runner already waits on this task, and swapping
            // an unanswered task strands that wait: the pod would then hold the lease with nothing running.
            if (gained.Task.IsCompleted) gained = NewGained();
        }
        ended?.Cancel();
        ended?.Dispose();
    }

    private static TaskCompletionSource<CancellationToken> NewGained() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
