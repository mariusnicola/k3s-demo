using BatchTracker.Application;
using BatchTracker.Infrastructure;
using BatchTracker.Live;
using BatchTracker.Line;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BatchTracker.UnitTests;

/// <summary>What each service reports and how the reporter behaves.</summary>
public class ServiceLogTests : ServiceLogTest
{
    [Fact]
    public void REQ_BT_053_An_error_becomes_an_entry_with_service_pod_and_id()
    {
        using var action = Correlation.Use("abc-1");

        Logger().LogError("Store failed");

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(ServiceLogLevel.Error, entry.Level);
        Assert.Equal("Store failed", entry.Message);
        Assert.Equal("api", entry.Service);
        Assert.Equal("api-1", entry.Pod);
        Assert.Equal("abc-1", entry.CorrelationId);
    }

    [Fact]
    public void REQ_BT_053_A_warning_becomes_an_entry_and_info_does_not()
    {
        var logger = Logger();

        logger.LogInformation("fine");
        logger.LogWarning("careful");

        Assert.Equal(ServiceLogLevel.Warning, Assert.Single(sink.Entries).Level);
    }

    [Fact]
    public void REQ_BT_053_The_exception_stays_in_its_own_fields_masked()
    {
        var failure = new InvalidOperationException("AccountKey=abc123==;rest");

        Logger().LogError(failure, "Store failed");

        var entry = Assert.Single(sink.Entries);
        Assert.Equal("Store failed", entry.Message);
        Assert.Equal(nameof(InvalidOperationException), entry.ExceptionType);
        Assert.DoesNotContain("abc123", entry.ExceptionMessage);
        Assert.Contains(Secrets.Mask, entry.ExceptionMessage);
    }

    [Fact]
    public void REQ_BT_053_A_secret_in_the_message_is_masked()
    {
        Logger().LogError("Header was Bearer abc.def");

        Assert.DoesNotContain("abc.def", Assert.Single(sink.Entries).Message);
    }

    [Fact]
    public void REQ_BT_053_A_very_long_message_is_cut()
    {
        Logger().LogError(new string('x', 10_000));

        Assert.True(Assert.Single(sink.Entries).Message.Length <= ServiceLogProvider.MaxMessage);
    }

    [Theory]
    [InlineData("System.Net.Http.HttpClient.Default")]
    [InlineData("BatchTracker.Infrastructure.RetryHandler")]
    public void REQ_BT_053_The_trouble_of_reporting_is_not_reported(string category)
    {
        Logger(category).LogError("Live is away");

        Assert.Empty(sink.Entries);
    }

    [Fact]
    public void REQ_BT_053_A_log_written_while_reporting_does_not_loop()
    {
        var provider = (ILoggerProvider?)null;
        var calls = 0;
        provider = new ServiceLogProvider(() =>
        {
            calls++;
            provider!.CreateLogger("BatchTracker.Test").LogError("while building the sink");
            return sink;
        }, "api", "api-1");

        provider.CreateLogger("BatchTracker.Test").LogError("first");

        Assert.Equal(1, calls);
        Assert.Single(sink.Entries);
    }

    [Fact]
    public void REQ_BT_053_Words_are_fixed_per_reason()
    {
        var error = Entry();
        var warning = Entry(ServiceLogLevel.Warning);

        Assert.False(string.IsNullOrWhiteSpace(error.Impact));
        Assert.False(string.IsNullOrWhiteSpace(error.NextAction));
        Assert.NotEqual(error.Impact, warning.Impact);
    }

    [Fact]
    public void REQ_BT_053_An_entry_has_every_field_of_a_line_entry()
    {
        var fields = typeof(ServiceLogEntry).GetProperties().Select(p => p.Name).ToHashSet();

        Assert.All(typeof(LineEntry).GetProperties(), p => Assert.Contains(p.Name, fields));
    }

    [Fact]
    public async Task REQ_BT_053_With_Live_away_a_stopping_pod_gives_an_entry_one_attempt()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddSingleton(new ReportBuffer());
        services.AddHttpClient<ReportFlusher>(h => h.BaseAddress = new Uri("http://live/")).AddRetries()
            .ConfigurePrimaryHttpMessageHandler(() => new Refuse(() => Interlocked.Increment(ref calls)));
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ReportBuffer>().Add(Entry());
        await provider.GetRequiredService<ReportFlusher>().StopAsync(default);

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task REQ_BT_053_A_report_waits_one_second_at_most()
    {
        var ended = new TaskCompletionSource<TimeSpan>();
        var services = new ServiceCollection();
        services.AddSingleton(new ReportBuffer());
        services.AddHttpClient<ReportFlusher>(h => h.BaseAddress = new Uri("http://live/")).AddRetries()
            .ConfigurePrimaryHttpMessageHandler(() => new Hang(ended));
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ReportBuffer>().Add(Entry());
        _ = provider.GetRequiredService<ReportFlusher>().FlushAsync(default);
        var waited = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.InRange(waited.TotalSeconds, 0.9, 1.5);
    }

    [Fact]
    public async Task REQ_BT_053_A_report_past_the_cap_is_dropped_and_never_sent()
    {
        var calls = 0;
        var reports = new ReportBuffer(cap: 0);
        using var http = new HttpClient(new Refuse(() => Interlocked.Increment(ref calls))) { BaseAddress = new Uri("http://live/") };

        reports.Add(Entry());
        await new ReportFlusher(http, reports).FlushAsync(default);

        Assert.Equal(1, reports.Dropped);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public void B_013_The_reporter_cuts_after_it_masks()
    {
        Logger().LogError("{Text}", new string('x', ServiceLogProvider.MaxMessage - 10) + "eyJabc.def.ghi");

        Assert.DoesNotContain("eyJ", Assert.Single(sink.Entries).Message);
    }
}
