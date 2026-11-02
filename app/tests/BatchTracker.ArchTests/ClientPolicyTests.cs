using BatchTracker.Api;
using BatchTracker.Application;
using BatchTracker.Dispatcher;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using BatchTracker.Live;
using BatchTracker.Worker;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BatchTracker.ArchTests;

/// <summary>Rules on the code around the routes: HTTP clients, counters, settings and folders.</summary>
public class ClientPolicyTests : EndpointPolicyTest
{
    [Fact]
    public void REQ_BT_050_Every_typed_client_goes_through_the_retry_handler()
    {
        var registrations = SourceFiles().Where(f => f.EndsWith(".cs"))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(f), @"AddHttpClient<.*?;", System.Text.RegularExpressions.RegexOptions.Singleline))
            .Select(m => m.Value)
            .ToList();

        Assert.NotEmpty(registrations);
        Assert.All(registrations, r => Assert.Contains(".AddRetries()", r));
    }

    [Fact]
    public void REQ_BT_050_No_client_is_built_by_hand()
    {
        Assert.DoesNotContain(
            SourceFiles().Where(f => f.EndsWith(".cs")),
            f => File.ReadAllText(f).Contains("new HttpClient("));
    }

    [Theory]
    [InlineData("HttpClients.cs", 2, 2, 0)]
    [InlineData("ReportBuffer.cs", 1, 1, 1)]
    [InlineData("LineApi.cs", 2, 2, 0)]
    public void REQ_BT_050_Every_post_says_how_it_is_safe_to_repeat(string file, int posts, int safeToRepeat, int singleAttempt)
    {
        var text = Source(file);

        Assert.Equal(posts, System.Text.RegularExpressions.Regex.Matches(text, @"HttpMethod\.Post").Count);
        Assert.Equal(safeToRepeat, System.Text.RegularExpressions.Regex.Matches(text, @"SafeToRepeat\(\)").Count);
        Assert.Equal(singleAttempt, System.Text.RegularExpressions.Regex.Matches(text, @"request\.SingleAttempt\(\)").Count);
        Assert.DoesNotContain("PostAsJsonAsync", text);
    }

    [Fact]
    public void REQ_BT_050_Attempt_limit_is_one_constant_every_sdk_reads()
    {
        Assert.Equal(10, RetryRules.MaxAttempts);
        Assert.Contains("MaxRetries = RetryRules.MaxAttempts - 1", Source("ServiceBusEventQueue.cs"));
        Assert.Contains("MaxRetryAttemptsOnRateLimitedRequests = RetryRules.MaxAttempts - 1", Source("CosmosBatchStore.cs"));
    }

    [Fact]
    public void REQ_BT_046_Published_table_names_exactly_the_counters_each_service_exports()
    {
        var doc = File.ReadAllLines(Path.Combine(Root, "..", "docs", "architecture", "monitoring.md"));
        var rows = doc.Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^\| `(bt_\w+)` \|.*\| (\w+) \|$"))
            .Where(m => m.Success)
            .Select(m => (Name: m.Groups[1].Value, Service: m.Groups[2].Value))
            .ToList();
        string[] Published(string service) => rows.Where(r => r.Service == service).Select(r => r.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(MetricNames.Api.Order(StringComparer.Ordinal), Published("API"));
        Assert.Equal(MetricNames.Worker, Published("Worker"));
        Assert.Equal(MetricNames.Dispatcher, Published("Dispatcher"));
    }

    [Fact]
    public void REQ_BT_051_Every_setting_the_code_requires_is_declared_for_validation()
    {
        var declared = new[] { ServiceSettings.Api, ServiceSettings.Worker, ServiceSettings.Dispatcher, ServiceSettings.Live, ServiceSettings.Line }
            .SelectMany(s => s).Where(s => s.Required).Append(ServiceSettings.CosmosConnection).Append(ServiceSettings.MongoConnection).Select(s => s.Name).ToHashSet();

        var read = SourceFiles().Where(f => f.EndsWith(".cs"))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "Require\\(\"(\\w+)\"\\)"))
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(read);
        Assert.DoesNotContain(read, name => !declared.Contains(name));
    }

    [Fact]
    public void REQ_BT_051_Every_service_validates_its_settings_first()
    {
        foreach (var service in new[] { "Api", "Worker", "Dispatcher", "Live", "Line" })
        {
            var program = SourceFiles().Single(f => f.EndsWith(Path.Combine($"BatchTracker.{service}", "Program.cs")));
            var text = File.ReadAllText(program);

            var check = text.IndexOf($"AddRequiredSettings(ServiceSettings.{service})", StringComparison.Ordinal);
            Assert.True(check >= 0, $"{service} does not validate its settings");
            var hosted = text.IndexOf("AddHostedService", StringComparison.Ordinal);
            Assert.True(hosted < 0 || check < hosted, $"{service} registers a hosted service before validation");
        }
    }

    [Fact]
    public void REQ_BT_051_Settings_come_only_from_the_environment()
    {
        var files = SourceFiles().ToList();

        Assert.DoesNotContain(files, f => Path.GetFileName(f).StartsWith("appsettings", StringComparison.Ordinal));
        Assert.DoesNotContain(files.Where(f => f.EndsWith(".cs")), f =>
            File.ReadAllText(f) is var text && (text.Contains("AddJsonFile") || text.Contains("AddUserSecrets") || text.Contains("GetEnvironmentVariable")));
    }
}
