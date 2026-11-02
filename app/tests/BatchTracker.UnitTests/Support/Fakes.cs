using System.Collections.Concurrent;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;

namespace BatchTracker.UnitTests;

public sealed class Clock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Now.UtcTicks;
}

public sealed class FakeStore : IBatchStore, IStoreReadings
{
    private readonly ConcurrentDictionary<string, Versioned> rows = new();
    private int version;

    /// <summary>The store's own clock, which no test pod shares.</summary>
    public DateTimeOffset ClockNow { get; set; }

    /// <summary>Every write fails, the way a store that went away does.</summary>
    public bool WritesFail { get; set; }

    /// <summary>The next count fails.</summary>
    public bool CountFails { get; set; }

    /// <summary>Runs while a count is in flight, so a test can change the world under it.</summary>
    public Action? WhileCounting { get; set; }

    public Task<DateTimeOffset> ClockAsync(CancellationToken token) => Task.FromResult(ClockNow);

    public Task<long> CountInProgressAsync(CancellationToken token)
    {
        if (CountFails) throw new InvalidOperationException("store down");
        WhileCounting?.Invoke();
        return Task.FromResult((long)rows.Values.Count(r => r.Batch.Pending?.Status is PendingStatus.Pending or PendingStatus.Sent));
    }

    /// <summary>Runs just before a replace, so a test can change the document under the writer.</summary>
    public Func<Batch, Task>? BeforeReplace { get; set; }
    public int LostWrites { get; private set; }

    /// <summary>Every replace loses, the way a row moved on by another writer does.</summary>
    public bool LoseEveryWrite { get; set; }

    public Versioned Put(Batch batch)
    {
        var row = new Versioned(batch, $"etag-{Interlocked.Increment(ref version)}");
        rows[batch.Id] = row;
        return row;
    }

    public Batch Read(string id) => rows[id].Batch;
    public string ETag(string id) => rows[id].ETag;

    public Task<Versioned?> CreateAsync(Batch batch, CancellationToken token) =>
        Task.FromResult(rows.ContainsKey(batch.Id) ? null : Put(batch));

    public Task<Versioned?> GetAsync(string farmId, string id, CancellationToken token) =>
        Task.FromResult(rows.GetValueOrDefault(id));

    public async Task<Versioned?> ReplaceAsync(Batch batch, string etag, CancellationToken token)
    {
        if (WritesFail) throw new InvalidOperationException("store down");
        if (LoseEveryWrite) return null;
        if (BeforeReplace is { } hook)
        {
            BeforeReplace = null;
            await hook(batch);
        }
        if (rows[batch.Id].ETag != etag)
        {
            LostWrites++;
            return null;
        }
        if (batch.Pending?.Status == PendingStatus.Pending) Interlocked.Increment(ref Returned);
        return Put(batch);
    }

    /// <summary>How many writes put an event back to pending.</summary>
    public int Returned;

    /// <summary>How many list reads and list writes ran, whatever their length.</summary>
    public int ManyReads;
    public int ManyWrites;

    public Task<IReadOnlyList<Versioned>> GetManyAsync(IReadOnlyList<BatchKey> keys, CancellationToken token)
    {
        Interlocked.Increment(ref ManyReads);
        return Task.FromResult<IReadOnlyList<Versioned>>(keys.Select(k => rows.GetValueOrDefault(k.Id)).OfType<Versioned>().ToList());
    }

    public async Task<IReadOnlyList<Versioned?>> WriteManyAsync(IReadOnlyList<BundleWrite> writes, CancellationToken token)
    {
        Interlocked.Increment(ref ManyWrites);
        var saved = new List<Versioned?>();
        foreach (var write in writes)
            saved.Add(write.ETag is null ? await CreateAsync(write.Batch, token) : await ReplaceAsync(write.Batch, write.ETag, token));
        return saved;
    }

    public Task<IReadOnlyList<Versioned>> ListAsync(BatchFilter filter, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<Versioned>>(rows.Values.ToList());

    /// <summary>How many sweeps read for due rows.</summary>
    public int DueReads;

    /// <summary>Every sweep read fails, the way a store that went away does.</summary>
    public bool ReadsFail { get; set; }

    public Task<IReadOnlyList<Versioned>> FindByPendingStatusAsync(string status, int max, CancellationToken token)
    {
        Interlocked.Increment(ref DueReads);
        if (ReadsFail) throw new InvalidOperationException("store down");
        return Task.FromResult<IReadOnlyList<Versioned>>(
            rows.Values.Where(r => r.Batch.Pending?.Status == status).Take(max).ToList());
    }

    public Task<IReadOnlyList<Versioned>> FindSentBeforeAsync(DateTimeOffset cutoff, int max, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<Versioned>>(rows.Values
            .Where(r => r.Batch.Pending?.Status == PendingStatus.Sent && r.Batch.Pending.SentAt < cutoff)
            .Take(max).ToList());

    /// <summary>How many writes the sweeps' marks took.</summary>
    public int Marks;

    public async Task<IReadOnlyList<Versioned>> MarkSentAsync(IReadOnlyList<Versioned> marking, DateTimeOffset at, CancellationToken token)
    {
        Interlocked.Increment(ref Marks);
        var won = new List<Versioned>();
        foreach (var row in marking)
        {
            var moved = row.Batch with { Pending = row.Batch.Pending! with { Status = PendingStatus.Sent, SentAt = at } };
            if (await ReplaceAsync(moved, row.ETag, token) is not null) won.Add(row);
        }
        return won;
    }

    public Task<IReadOnlyList<Versioned>> FindInStateAsync(BatchState state, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<Versioned>>(rows.Values.Where(r => r.Batch.State == state).ToList());

    /// <summary>Answers the scan in place of the rows held, so a test can hold counts for more batches than rows.</summary>
    public LiveScan? LiveAnswer { get; set; }

    public Task<LiveScan> FindForLiveAsync(CancellationToken token) => Task.FromResult(LiveAnswer ?? Scan.Of(rows.Values.ToList()));

    /// <summary>How many times every row was read, the read that cannot be allowed on a request.</summary>
    public int FullReads;

    public Task<IReadOnlyList<Batch>> FindForReportsAsync(IReadOnlyList<string>? farms, CancellationToken token)
    {
        Interlocked.Increment(ref FullReads);
        return Task.FromResult<IReadOnlyList<Batch>>(rows.Values.Select(r => r.Batch).ToList());
    }

    /// <summary>What a reachability check does, so a test can make it slow or fail.</summary>
    public Func<CancellationToken, Task<bool>> Reach { get; set; } = _ => Task.FromResult(true);

    public Task<bool> ReachableAsync(CancellationToken token) => Reach(token);
}

public sealed class FakeDelivery(BatchEvent message) : IDelivery
{
    public BatchEvent Event => message;
    public string? Settled { get; private set; }

    public Task CompleteAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Settled = "complete";
        return Task.CompletedTask;
    }
}

/// <summary>One object plays the bus across connections, so messages survive a lost lease like a real queue.</summary>
public sealed class FakeBus : IEventQueueFactory
{
    public ConcurrentQueue<BatchEvent> Sent { get; } = new();
    public ConcurrentQueue<FakeDelivery> Incoming { get; } = new();
    public ConcurrentQueue<FakeDelivery> Letters { get; } = new();
    public ConcurrentQueue<FakeDelivery> Received { get; } = new();

    /// <summary>How many events one received message holds.</summary>
    public int PackSize { get; set; } = int.MaxValue;

    /// <summary>How many times a pack was asked for.</summary>
    public int Receives;

    public bool Down { get; set; }
    public bool SendFails { get; set; }

    /// <summary>Fails only the sends it names, the way one bad send fails among good ones.</summary>
    public Func<BatchEvent, bool>? FailSend { get; set; }

    /// <summary>Runs before received messages reach the caller, so a test can change the world while they are on the way.</summary>
    public Func<IReadOnlyList<FakeDelivery>, Task>? WhileReceiving { get; set; }

    /// <summary>How long a send takes, so sends can overlap.</summary>
    public TimeSpan SendTime { get; set; }

    /// <summary>How many sends the bus was asked for.</summary>
    public int BatchSends;

    /// <summary>How many dead letters the last read asked for.</summary>
    public volatile int DeadLetterRead;
    public int Connects;
    public int OpenConnections;

    /// <summary>A real bus redelivers what was abandoned, so sends and abandons loop straight back.</summary>
    public bool EchoSends { get; set; } = true;

    public Task<IEventQueue> ConnectAsync(CancellationToken token)
    {
        Interlocked.Increment(ref Connects);
        if (Down) throw new InvalidOperationException("bus down");
        Interlocked.Increment(ref OpenConnections);
        return Task.FromResult<IEventQueue>(new Connection(this));
    }

    private sealed class Connection(FakeBus bus) : IEventQueue
    {
        /// <summary>All or none, the way one bus batch goes.</summary>
        public async Task SendAsync(IReadOnlyList<BatchEvent> messages, CancellationToken token)
        {
            Interlocked.Increment(ref bus.BatchSends);
            if (bus.Down || bus.SendFails) throw new InvalidOperationException("bus down");
            if (bus.SendTime > TimeSpan.Zero) await Task.Delay(bus.SendTime, token);
            if (messages.Any(m => bus.FailSend?.Invoke(m) == true)) throw new InvalidOperationException("bus down");
            foreach (var message in messages)
            {
                bus.Sent.Enqueue(message);
                if (bus.EchoSends) bus.Incoming.Enqueue(new FakeDelivery(message));
            }
        }

        public async Task<IReadOnlyList<IDelivery>> ReceiveAsync(TimeSpan wait, CancellationToken token)
        {
            Interlocked.Increment(ref bus.Receives);
            if (bus.Down) throw new InvalidOperationException("bus down");
            var taken = Take(bus.Incoming, bus.PackSize);
            foreach (var delivery in taken) bus.Received.Enqueue(delivery);
            if (taken.Count == 0) await Task.Delay(wait, token);
            else if (bus.WhileReceiving is { } hold) await hold(taken);
            return taken;
        }

        public async Task<IReadOnlyList<IDelivery>> ReceiveDeadLettersAsync(int max, TimeSpan wait, CancellationToken token)
        {
            bus.DeadLetterRead = max;
            var taken = Take(bus.Letters, max);
            if (taken.Count == 0) await Task.Delay(wait, token);
            return taken;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Decrement(ref bus.OpenConnections);
            return ValueTask.CompletedTask;
        }

        private static List<FakeDelivery> Take(ConcurrentQueue<FakeDelivery> from, int max)
        {
            var taken = new List<FakeDelivery>();
            while (taken.Count < max && from.TryDequeue(out var delivery)) taken.Add(delivery);
            return taken;
        }
    }
}

public sealed class FakeWorker : IWorkerClient
{
    public Func<BatchEvent, CancellationToken, Task<WorkerResult>> Handler { get; set; } =
        (_, _) => Task.FromResult(WorkerResult.Done);
    public bool Ready { get; set; } = true;
    public int ReadyCalls;

    public Task<WorkerResult> PostEventAsync(BatchEvent message, CancellationToken token) => Handler(message, token);

    public Task<bool> ReadyAsync(CancellationToken token)
    {
        Interlocked.Increment(ref ReadyCalls);
        return Task.FromResult(Ready);
    }

    public Task<ComplianceResult> RunNightlyAsync(CancellationToken token) => Task.FromResult(new ComplianceResult(0));
}

/// <summary>The scan a store would answer holding only these rows.</summary>
public static class Scan
{
    /// <summary>Counts these rows the way the store would.</summary>
    public static LiveScan Of(params IReadOnlyList<Versioned> rows) => new(
        rows.GroupBy(r => (r.Batch.FarmId, r.Batch.State)).Select(g => new StateCount(g.Key.FarmId, g.Key.State.ToString(), g.Count())).ToList(),
        rows);
}
