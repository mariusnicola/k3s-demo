using BatchTracker.Application;
using BatchTracker.Infrastructure;
using System.Text.RegularExpressions;
using BatchTracker.Live;

namespace BatchTracker.UnitTests;

/// <summary>Live trusts no entry: whatever arrives is masked, cut and checked here.</summary>
public class ServiceLogEntryTests
{
    /// <summary>An entry written by hand, every field far past its limit.</summary>
    private static ServiceLogEntry Forged() => new(
        DateTimeOffset.UnixEpoch, ServiceLogLevel.Error, ServiceLogLevel.ErrorReason, "Bearer abc.def " + Huge, "api", Huge, Huge, Huge, "AccountKey=abc123;" + Huge,
        Huge, BatchId: Huge, Farm: Huge, Step: Huge);

    private static readonly string Huge = new('x', 1_000_000);

    /// <summary>All the text an entry holds.</summary>
    private static int TextOf(ServiceLogEntry e) => new[]
    {
        e.Message, e.Pod, e.CorrelationId, e.ExceptionType, e.ExceptionMessage, e.TraceId, e.BatchId, e.Farm, e.Step
    }.Sum(t => t?.Length ?? 0);

    [Fact]
    public void B_013_Live_masks_an_entry_itself()
    {
        var log = new ServiceLog();

        log.Add(Forged());

        var entry = Assert.Single(log.Query(new ServiceLogQuery()).Entries);
        Assert.DoesNotContain("abc.def", entry.Message);
        Assert.DoesNotContain("abc123", entry.ExceptionMessage);
    }

    [Fact]
    public void B_013_Live_cuts_every_field_of_a_forged_entry()
    {
        var log = new ServiceLog();

        log.Add(Forged());

        var entry = Assert.Single(log.Query(new ServiceLogQuery()).Entries);
        Assert.True(TextOf(entry) <= ServiceLog.MaxEntryText);
    }

    [Fact]
    public void B_013_Forged_entries_cannot_grow_the_log_past_its_cap()
    {
        var log = new ServiceLog();

        for (var i = 0; i < ServiceLog.Capacity + 100; i++) log.Add(Forged());

        var held = log.Query(new ServiceLogQuery(Limit: ServiceLog.Capacity)).Entries;
        Assert.Equal(ServiceLog.Capacity, held.Count);
        Assert.True(held.Sum(TextOf) <= ServiceLog.Capacity * ServiceLog.MaxEntryText);
    }

    [Theory]
    [InlineData("nope", ServiceLogLevel.ErrorReason, "api", "level")]
    [InlineData(ServiceLogLevel.Error, "nope", "api", "reason")]
    [InlineData(ServiceLogLevel.Error, ServiceLogLevel.ErrorReason, "nope", "service")]
    public void B_013_A_bad_field_is_named(string level, string reason, string service, string named)
    {
        var entry = ServiceLogTests.Entry() with { Level = level, Reason = reason, Service = service };

        Assert.Contains(named, ServiceLog.Problem(entry));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("worker")]
    [InlineData("dispatcher")]
    [InlineData("live")]
    public void B_013_Each_real_service_passes(string service)
    {
        Assert.Null(ServiceLog.Problem(ServiceLogTests.Entry(service: service)));
    }

    [Fact]
    public void B_037_The_mask_runs_on_the_engine_that_cannot_backtrack()
    {
        Assert.True(Secrets.TokenOptions.HasFlag(RegexOptions.NonBacktracking));
    }

    [Fact]
    public void B_037_Text_past_the_room_is_never_masked_so_a_flood_costs_no_work()
    {
        var secret = "Bearer " + new string('a', 1000) + " ";

        var cut = Secrets.MaskAndCut(string.Concat(Enumerable.Repeat(secret, 1000)), 2000);

        var masks = cut.Split(Secrets.Mask).Length - 1;
        Assert.InRange(masks, 1, (2000 + Secrets.SecretRoom) / secret.Length + 1);
    }

    [Fact]
    public void T_017_A_long_token_starting_inside_the_limit_is_masked()
    {
        var token = "eyJ" + new string('a', 3000) + ".def.ghi";

        var cut = Secrets.MaskAndCut(new string('x', 1990) + token, 2000);

        Assert.DoesNotContain("eyJ", cut);
    }

    [Theory]
    [InlineData("")]
    [InlineData("before ")]
    public void T_017_A_token_longer_than_the_room_leaves_nothing_in_the_log(string before)
    {
        var token = "eyJ" + new string('a', 20_000) + ".def.ghi";

        var cut = Secrets.MaskAndCut(before + token, 2000);

        Assert.Equal(before + Secrets.Mask, cut);
    }

    [Theory]
    [InlineData("eyJ" + "aaaaaaaaaaaaaaaa", Secrets.Mask)]
    [InlineData("eyJ" + "aaaaaaaaaaaaaaa", "eyJaaaaaaaaaaaaaaa")]
    [InlineData("eyJust a word", "eyJust a word")]
    [InlineData("KeyJsonConverterFactoryOptions", "KeyJsonConverterFactoryOptions")]
    [InlineData("EYJAAAAAAAAAAAAAAAAAAAAA", "EYJAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("token=eyJaaaaaaaaaaaaaaaa", "token=" + Secrets.Mask)]
    public void T_017_Dotless_text_is_masked_from_a_run_of_19_characters(string text, string expected)
    {
        Assert.Equal(expected, Secrets.Redact(text));
    }

    [Theory]
    [InlineData("Bearer ")]
    [InlineData("AccountKey=")]
    public void T_017_A_keyed_secret_longer_than_the_room_is_still_masked(string key)
    {
        var cut = Secrets.MaskAndCut(key + new string('a', 20_000), 2000);

        Assert.DoesNotContain("aaa", cut);
    }

    [Fact]
    public void B_013_A_cut_never_splits_a_secret()
    {
        var token = "eyJabc.def.ghi";

        var cut = Secrets.MaskAndCut(new string('x', 1990) + token, 2000);

        Assert.DoesNotContain("eyJ", cut);
        Assert.True(cut.Length <= 2000);
    }
}
