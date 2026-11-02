using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.UnitTests;

public sealed class FakeHops : IHopReporter
{
    public System.Collections.Concurrent.ConcurrentQueue<Hop> Hops { get; } = new();
    public void Report(Hop hop) => Hops.Enqueue(hop);
}

public sealed class FakeCounters : IBatchCounters
{
    public List<(string Name, string Farm)> Counted { get; } = [];
    public List<(string Stage, double Seconds)> Times { get; } = [];
    public void Count(string name, string farm) => Counted.Add((name, farm));
    public void Time(string stage, double seconds) => Times.Add((stage, seconds));
}
