using BatchTracker.Application;
using BatchTracker.Domain;

namespace BatchTracker.IntegrationTests;

/// <summary>The sweep takes a bounded number of rows, so which rows it reads first decides who waits.</summary>
public class OutboxOrderTests(Stack stack) : StackTest(stack)
{
    private static readonly TimeSpan NextSecond = TimeSpan.FromSeconds(1.1);

    private static Batch Waiting(string farm, string status, DateTimeOffset? sentAt = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        FarmId = farm,
        Product = "p",
        State = BatchState.Created,
        CreatedAt = DateTimeOffset.UtcNow,
        Pending = new PendingEvent { EventId = Guid.NewGuid().ToString(), TraceId = "t", Status = status, SentAt = sentAt }
    };

    private Task WriteAsync(params Batch[] batches) =>
        Task.WhenAll(batches.Select(b => Stack.Store.CreateAsync(b, default)));

    [Theory]
    [InlineData("farm-a")]
    [InlineData("farm-b")]
    [InlineData("farm-c")]
    public async Task REQ_BT_009_The_event_waiting_longest_goes_first(string farm)
    {
        var oldest = Waiting(farm, PendingStatus.Pending);
        await WriteAsync(oldest);
        await Task.Delay(NextSecond);
        await WriteAsync(DemoUsers.KnownFarms.Select(f => Waiting(f, PendingStatus.Pending)).ToArray());

        var due = await Stack.Store.FindByPendingStatusAsync(PendingStatus.Pending, 1, default);

        Assert.Equal(oldest.Id, Assert.Single(due).Batch.Id);
    }

    [Theory]
    [InlineData("farm-a")]
    [InlineData("farm-b")]
    [InlineData("farm-c")]
    public async Task REQ_BT_009_The_stale_event_sent_longest_ago_goes_first(string farm)
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var oldest = Waiting(farm, PendingStatus.Sent, start);
        await WriteAsync([.. DemoUsers.KnownFarms.Select(f => Waiting(f, PendingStatus.Sent, start.AddMinutes(1)))]);
        await WriteAsync(oldest);

        var stale = await Stack.Store.FindSentBeforeAsync(DateTimeOffset.UtcNow, 1, default);

        Assert.Equal(oldest.Id, Assert.Single(stale).Batch.Id);
    }

    [Fact]
    public async Task REQ_BT_009_Age_runs_from_the_last_write_not_the_first()
    {
        var rewritten = Waiting("farm-a", PendingStatus.Pending);
        var created = await Stack.Store.CreateAsync(rewritten, default);
        await Task.Delay(NextSecond);
        var oldest = Waiting("farm-a", PendingStatus.Pending);
        await WriteAsync(oldest);
        await Task.Delay(NextSecond);
        await Stack.Store.ReplaceAsync(rewritten, created!.ETag, default);

        var due = await Stack.Store.FindByPendingStatusAsync(PendingStatus.Pending, 1, default);

        Assert.Equal(oldest.Id, Assert.Single(due).Batch.Id);
    }

    [Fact]
    public async Task REQ_BT_009_A_read_never_returns_more_than_it_was_asked_for()
    {
        await WriteAsync([.. Enumerable.Range(0, 9).Select(i => Waiting(DemoUsers.KnownFarms[i % DemoUsers.KnownFarms.Count], PendingStatus.Pending))]);

        Assert.Equal(4, (await Stack.Store.FindByPendingStatusAsync(PendingStatus.Pending, 4, default)).Count);
    }
}
