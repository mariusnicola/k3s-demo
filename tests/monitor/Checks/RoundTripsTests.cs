using System.Diagnostics;
using Xunit;

public class RoundTripsTests
{
    /// <summary>Nothing listens on this address, so the connection is refused and a real emulator is never reached.</summary>
    private const string Nowhere = "127.0.0.2";

    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(300);
    private const double MostSeconds = 1.5;

    private static async Task AssertEndsAtTheLimit(Func<CancellationToken, Task> trip)
    {
        using var limit = new CancellationTokenSource(Limit);
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trip(limit.Token));

        Assert.InRange(clock.Elapsed.TotalSeconds, 0, MostSeconds);
    }

    [Fact]
    public async Task B_064_a_slow_start_of_a_trip_does_not_hold_the_caller()
    {
        using var returned = new ManualResetEventSlim();
        var held = false;

        var trip = RoundTrips.Limited(() =>
        {
            held = !returned.Wait(TimeSpan.FromSeconds(5));
            return Task.CompletedTask;
        }, CancellationToken.None);
        returned.Set();
        await trip;

        Assert.False(held);
    }

    [Fact]
    public Task B_064_a_refused_service_bus_ends_at_the_limit() =>
        AssertEndsAtTheLimit(token => RoundTrips.ServiceBus($"{Nowhere}:5682", "monitor", token));

    [Fact]
    public async Task B_064_a_refusal_inside_the_limit_still_gives_the_service_error()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<Azure.Messaging.ServiceBus.ServiceBusException>(() => RoundTrips.ServiceBus($"{Nowhere}:5682", "monitor", limit.Token));
    }

    [Fact]
    public Task B_064_a_refused_cosmos_ends_at_the_limit() =>
        AssertEndsAtTheLimit(token => RoundTrips.Cosmos($"{Nowhere}:8091", "monitor", token));

    [Fact]
    public Task A_refused_mongo_ends_at_the_limit() =>
        AssertEndsAtTheLimit(token => RoundTrips.Mongo($"mongodb://{Nowhere}:27018/?serverSelectionTimeoutMS=30000", "monitor", token));

    [Fact]
    public async Task A_mongo_failure_never_carries_the_connection()
    {
        const string connection = $"mongodb://stand-in-user:stand-in-value@{Nowhere}:27018/?serverSelectionTimeoutMS=200&retryWrites=false";
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RoundTrips.Mongo(connection, "monitor", limit.Token));

        Assert.DoesNotContain("stand-in", failure.Message);
        Assert.DoesNotContain(Nowhere, failure.Message);
        Assert.Null(failure.InnerException);
    }
}
