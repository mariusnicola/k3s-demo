using BatchTracker.Infrastructure;
using BatchTracker.Line;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BatchTracker.UnitTests;

public class ProblemWordsTests
{
    private static HttpContext Request(string method, string path, int status = 200)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.StatusCode = status;
        return context;
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(409)]
    [InlineData(412)]
    [InlineData(415)]
    [InlineData(422)]
    [InlineData(428)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void REQ_BT_053_Each_status_says_what_happened_and_what_to_do(int status)
    {
        Assert.Contains(status, ProblemWords.Statuses);
        Assert.NotEqual(ProblemWords.HappenedFor(418), ProblemWords.HappenedFor(status));
        Assert.False(string.IsNullOrWhiteSpace(ProblemWords.NextFor(status)));
    }

    [Fact]
    public void REQ_BT_053_A_status_with_no_text_still_gets_plain_words()
    {
        Assert.False(string.IsNullOrWhiteSpace(ProblemWords.HappenedFor(418)));
        Assert.False(string.IsNullOrWhiteSpace(ProblemWords.NextFor(418)));
    }

    [Theory]
    [InlineData("GET", "/api/me", 401, ProblemWords.NoBatch)]
    [InlineData("POST", "/api/compliance/run", 500, ProblemWords.FlagsMayHaveChanged)]
    [InlineData("POST", "/api/compliance/run", 403, ProblemWords.NoBatch)]
    [InlineData("POST", "/nightly/run", 500, ProblemWords.FlagsMayHaveChanged)]
    [InlineData("POST", "/nightly/run", 503, ProblemWords.FlagsMayHaveChanged)]
    [InlineData("POST", "/nightly/run", 401, ProblemWords.NoBatch)]
    [InlineData("POST", "/events", 500, ProblemWords.MayHaveChanged)]
    [InlineData("POST", "/line", 500, ProblemWords.NoBatch)]
    [InlineData("DELETE", "/line", 500, ProblemWords.NoBatch)]
    [InlineData("POST", "/api/auth/dev-login", 500, ProblemWords.NoBatch)]
    [InlineData("GET", "/api/batches", 500, ProblemWords.OnlyRead)]
    [InlineData("POST", "/api/batches/farm-a/b/advance", 409, ProblemWords.NotChanged)]
    [InlineData("POST", "/api/batches/farm-a/b/advance", 500, ProblemWords.MayHaveChanged)]
    public void REQ_BT_053_The_impact_follows_the_route_method_and_status(string method, string path, int status, string impact) =>
        Assert.Equal(impact, ProblemWords.ImpactOf(method, path, status));

    [Fact]
    public void REQ_BT_053_A_500_never_keeps_the_text_it_came_with()
    {
        var problem = new ProblemDetails { Status = 500, Detail = "AccountKey=abc at Store.Read()" };

        ProblemWords.Apply(problem, Request("GET", "/api/batches", 500));

        Assert.Equal(ProblemWords.HappenedFor(500), problem.Detail);
    }

    [Fact]
    public void REQ_BT_053_A_route_keeps_its_own_plain_detail()
    {
        var problem = new ProblemDetails { Status = 400, Detail = "farmId and product are required." };

        ProblemWords.Apply(problem, Request("POST", "/api/batches", 400));

        Assert.Equal("farmId and product are required.", problem.Detail);
        Assert.Equal("Creating a batch", problem.Extensions["where"]);
        Assert.Equal("POST /api/batches", problem.Extensions["path"]);
        Assert.Equal(ProblemWords.NotChanged, problem.Extensions["impact"]);
        Assert.Equal(ProblemWords.NextFor(400), problem.Extensions["next"]);
    }

    [Theory]
    [InlineData("POST", "/api/auth/dev-login", "Signing in")]
    [InlineData("POST", "/api/auth/machine", "Signing in")]
    [InlineData("POST", "/api/auth/logout", "Signing out")]
    [InlineData("GET", "/api/me", "Reading who is signed in")]
    [InlineData("GET", "/api/batches", "Reading the batch list")]
    [InlineData("POST", "/api/batches", "Creating a batch")]
    [InlineData("GET", "/api/batches/farm-a/b1", "Reading a batch on farm-a")]
    [InlineData("POST", "/api/batches/farm-a/b1/advance", "Moving a batch on farm-a")]
    [InlineData("POST", "/api/batches/farm-b/b1/inspect", "Recording an inspection on farm-b")]
    [InlineData("POST", "/api/batches/farm-a/b1/fail-next", "Setting a batch on farm-a to fail its next event")]
    [InlineData("POST", "/api/batches/farm-a/b1/replay", "Replaying a blocked batch on farm-a")]
    [InlineData("GET", "/api/summary", "Reading the summary")]
    [InlineData("GET", "/api/alerts", "Reading the alerts")]
    [InlineData("POST", "/api/compliance/run", "Running the compliance check")]
    [InlineData("POST", "/nightly/run", "Running the nightly compliance check")]
    [InlineData("POST", "/events", "Recording a batch event")]
    [InlineData("GET", "/api/ready", "Checking the service is ready")]
    [InlineData("GET", "/ready", "Checking the service is ready")]
    [InlineData("GET", "/healthz", "Checking the service is up")]
    [InlineData("GET", "/metrics", "Reading the metrics")]
    [InlineData("GET", "/status", "Reading the Dispatcher status")]
    [InlineData("GET", "/line", "Reading the Line status")]
    [InlineData("POST", "/line", "Starting the Line")]
    [InlineData("DELETE", "/line", "Stopping the Line")]
    [InlineData("GET", "/line/logs", "Reading the Line log")]
    [InlineData("GET", "/line/ws", "Opening the Line feed")]
    [InlineData("GET", "/live/logs", "Reading the service log")]
    [InlineData("GET", "/ws/hops", "Opening the live feed")]
    [InlineData("POST", "/reports", "Reporting to Live")]
    [InlineData("POST", "/hops", "Reporting to Live")]
    public void T_013_Where_says_what_was_being_done(string method, string path, string words) =>
        Assert.Equal(words, ProblemPlaces.Of(method, path));

    [Fact]
    public void T_013_A_farm_nobody_knows_is_not_repeated()
    {
        Assert.Equal("Moving a batch on a farm", ProblemPlaces.Of("POST", "/api/batches/<b>x/b1/advance"));
    }

    [Fact]
    public void T_013_A_path_with_no_words_still_reads_plain()
    {
        Assert.Equal(ProblemPlaces.Unknown, ProblemPlaces.Of("GET", "/nothing/here"));
        Assert.DoesNotContain("/", ProblemPlaces.Unknown);
    }

    [Theory]
    [InlineData("30", "Wait 30 seconds, then try again.")]
    [InlineData("1", "Wait 1 second, then try again.")]
    public void T_013_A_429_says_the_wait_the_header_carries(string seconds, string words)
    {
        var context = Request("GET", "/api/me", 429);
        context.Response.Headers.RetryAfter = seconds;
        var problem = new ProblemDetails { Status = 429 };

        ProblemWords.Apply(problem, context);

        Assert.Equal(words, problem.Extensions["next"]);
    }

    [Fact]
    public void T_013_A_429_with_no_wait_names_no_header()
    {
        var problem = new ProblemDetails { Status = 429 };

        ProblemWords.Apply(problem, Request("GET", "/api/me", 429));

        Assert.DoesNotContain("Retry-After", (string)problem.Extensions["next"]!);
    }

    [Fact]
    public void REQ_BT_053_Every_line_reason_has_its_own_impact_and_next_action()
    {
        foreach (var reason in LineReason.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(LineWords.ImpactOf(reason)), reason);
            Assert.False(string.IsNullOrWhiteSpace(LineWords.NextActionOf(reason)), reason);
        }
    }

    [Fact]
    public void REQ_BT_053_A_line_entry_carries_the_words_of_its_reason()
    {
        var entry = new LineEntry(
            DateTimeOffset.UnixEpoch, LineLevel.Error, LineReason.EventTimeout, "waited", 1, null, "farm-a", LineStep.Create, 0, null, null, null, null);

        Assert.Equal(LineWords.ImpactOf(LineReason.EventTimeout), entry.Impact);
        Assert.Equal(LineWords.NextActionOf(LineReason.EventTimeout), entry.NextAction);
    }

    [Theory]
    [InlineData("AccountKey=abc123==;more")]
    [InlineData("SharedAccessKey=abc123;more")]
    [InlineData("Bearer abc.def")]
    public void REQ_BT_053_Secrets_in_a_message_are_masked(string text)
    {
        var masked = Secrets.Redact(text);

        Assert.Contains(Secrets.Mask, masked);
        Assert.DoesNotContain("abc", masked);
    }
}
