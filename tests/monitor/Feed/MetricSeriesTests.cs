using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

/// <summary>Audit rows F14 and F15, and the REQ-BT-046 contract, against a Prometheus that answers from a table.</summary>
public class MetricSeriesTests
{
    private const string Lowest = "prometheus_tsdb_lowest_timestamp_seconds";
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string PublicKey = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 34, 56, TimeSpan.Zero);
    private static readonly DateTimeOffset LastWhole = new(2026, 10, 31, 12, 34, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Token(string[] permissions) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "batch-tracker",
        Audience = "batch-tracker",
        Subject = new ClaimsIdentity(new[] { new Claim(JwtRegisteredClaimNames.Sub, "ada") }.Concat(permissions.Select(p => new Claim("perm", p)))),
        Expires = DateTime.UtcNow.AddHours(1),
        SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(Key), SecurityAlgorithms.EcdsaSha256)
    });

    private static string Vector(double value) =>
        $"{{\"status\":\"success\",\"data\":{{\"resultType\":\"vector\",\"result\":[{{\"metric\":{{}},\"value\":[1,\"{value.ToString(CultureInfo.InvariantCulture)}\"]}}]}}}}";

    private static string Matrix(params (DateTimeOffset At, double Value)[] rows)
    {
        if (rows.Length == 0) return "{\"status\":\"success\",\"data\":{\"resultType\":\"matrix\",\"result\":[]}}";
        var values = string.Join(",", rows.Select(r => $"[{r.At.ToUnixTimeSeconds()},\"{r.Value.ToString(CultureInfo.InvariantCulture)}\"]"));
        return $"{{\"status\":\"success\",\"data\":{{\"resultType\":\"matrix\",\"result\":[{{\"metric\":{{}},\"values\":[{values}]}}]}}}}";
    }

    /// <summary>A Prometheus with long history and nothing to report unless a test says otherwise.</summary>
    private static Func<string, CancellationToken, Task<string>> Prometheus(string? events = null, DateTimeOffset? historyStart = null, List<string>? asked = null) =>
        (path, _) =>
        {
            asked?.Add(path);
            if (path.Contains(Lowest)) return Task.FromResult(Vector((historyStart ?? Now.AddDays(-200)).ToUnixTimeSeconds()));
            return Task.FromResult(path.Contains("bt_events_created_total") && events is not null ? events : Matrix());
        };

    private static MetricSeries Series(Func<string, CancellationToken, Task<string>> prometheus) => new(prometheus);

    private sealed record Reply(string Bucket, bool Available, string? Error, DateTimeOffset? HistoryStart, Row[] Series);
    private sealed record Row(string Name, double Total, double AveragePerMinute, Point[] Points);
    private sealed record Point(DateTimeOffset At, double? Value);

    private static async Task<MetricsReply> Read(string bucket, Func<string, CancellationToken, Task<string>> prometheus) =>
        await Series(prometheus).Read(bucket, Now, CancellationToken.None);

    private static MetricRow Events(MetricsReply reply) => reply.Series.Single(s => s.Name == "bt_events_created_total");

    [Fact]
    public async Task REQ_BT_046_every_metric_name_in_the_contract_comes_back()
    {
        var reply = await Read("1m", Prometheus());

        Assert.Equal(14, reply.Series.Length);
        Assert.Contains(reply.Series, s => s.Name == "bt_events_dead_lettered_total");
        Assert.Contains(reply.Series, s => s.Name == "bt_bundle_items_total");
        Assert.Contains(reply.Series, s => s.Name == "bt_bundle_item_failures_total");
        Assert.True(reply.Available);
    }

    [Theory]
    [InlineData("1m", 60)]
    [InlineData("30m", 1800)]
    [InlineData("1h", 3600)]
    [InlineData("3h", 10800)]
    [InlineData("1d", 86400)]
    public async Task REQ_BT_046_120_buckets_of_increase_summed_over_pods_ending_at_the_last_whole_bucket(string bucket, int seconds)
    {
        var asked = new List<string>();
        var reply = await Read(bucket, Prometheus(asked: asked));

        var query = HttpUtility.ParseQueryString(asked.First(a => a.Contains("bt_events_created_total")).Split('?')[1]);
        Assert.Equal($"sum(increase(bt_events_created_total[{seconds}s]))", query["query"]);
        Assert.Equal(seconds.ToString(), query["step"]);
        var points = Events(reply).Points;
        Assert.Equal(120, points.Length);
        Assert.Equal(TimeSpan.FromSeconds(seconds), points[1].At - points[0].At);
        Assert.Equal(0, points[^1].At.AddSeconds(seconds).ToUnixTimeSeconds() % seconds);
        Assert.True(points[^1].At.AddSeconds(seconds) <= Now);
        Assert.True(Now - points[^1].At.AddSeconds(seconds) < TimeSpan.FromSeconds(seconds));
        Assert.Equal(long.Parse(query["end"]!), points[^1].At.AddSeconds(seconds).ToUnixTimeSeconds());
        Assert.Equal(long.Parse(query["start"]!), points[0].At.AddSeconds(seconds).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task REQ_BT_046_a_point_is_the_bucket_start_and_holds_the_count_that_ended_with_it()
    {
        var reply = await Read("1m", Prometheus(Matrix((LastWhole, 7))));

        var last = Events(reply).Points[^1];
        Assert.Equal(LastWhole.AddMinutes(-1), last.At);
        Assert.Equal(7, last.Value);
    }

    [Fact]
    public async Task REQ_BT_046_a_bucket_prometheus_left_out_is_null_and_never_zero()
    {
        var reply = await Read("1m", Prometheus(Matrix((LastWhole, 7))));

        Assert.Null(Events(reply).Points[^2].Value);
    }

    [Fact]
    public async Task REQ_BT_046_a_bucket_before_history_starts_is_null_even_if_prometheus_answered()
    {
        var start = LastWhole.AddMinutes(-3);
        var reply = await Read("1m", Prometheus(Matrix((start.AddMinutes(-1), 9), (LastWhole, 2)), start));

        var points = Events(reply).Points;
        Assert.Null(points.Single(p => p.At == start.AddMinutes(-2)).Value);
        Assert.Equal(2, points[^1].Value);
        Assert.Equal(start, reply.HistoryStart);
    }

    [Fact]
    public async Task REQ_BT_046_total_is_the_sum_and_the_average_covers_only_minutes_since_history_began()
    {
        var start = LastWhole.AddMinutes(-4);
        var reply = await Read("1m", Prometheus(Matrix((LastWhole, 6), (LastWhole.AddMinutes(-1), 2)), start));

        Assert.Equal(8, Events(reply).Total);
        Assert.Equal(2, Events(reply).AveragePerMinute);
    }

    [Fact]
    public async Task REQ_BT_046_with_old_history_the_average_covers_the_first_bucket_on()
    {
        var reply = await Read("1m", Prometheus(Matrix((LastWhole, 240))));

        Assert.Equal(2, Events(reply).AveragePerMinute);
    }

    [Fact]
    public async Task REQ_BT_046_prometheus_down_is_unavailable_with_no_zeros()
    {
        var reply = await Read("1m", (_, _) => throw new HttpRequestException("refused"));

        Assert.False(reply.Available);
        Assert.Contains("refused", reply.Error);
        Assert.Empty(reply.Series);
        Assert.Null(reply.HistoryStart);
    }

    [Fact]
    public async Task REQ_BT_046_prometheus_saying_error_is_unavailable_too()
    {
        var reply = await Read("1m", (_, _) => Task.FromResult("{\"status\":\"error\",\"error\":\"too many samples\"}"));

        Assert.False(reply.Available);
        Assert.Contains("too many samples", reply.Error);
    }

    [Fact]
    public async Task REQ_BT_046_a_point_asks_each_metric_for_the_minute_that_closed()
    {
        var asked = new List<string>();
        var reply = await Series((path, _) =>
        {
            asked.Add(path);
            return Task.FromResult(path.Contains("bt_dose1_total") ? Vector(3.4) : Matrix());
        }).Closed(Now, CancellationToken.None);

        Assert.Equal(14, asked.Count);
        var query = HttpUtility.ParseQueryString(asked.First(a => a.Contains("bt_dose1_total")).Split('?')[1]);
        Assert.Equal("sum(increase(bt_dose1_total[60s]))", query["query"]);
        Assert.Equal(LastWhole.ToUnixTimeSeconds().ToString(), query["time"]);
        Assert.Equal(3, reply["bt_dose1_total"]);
        Assert.False(reply.ContainsKey("bt_dose2_total"));
    }

    [Theory]
    [InlineData(56, 24)]
    [InlineData(20, 60)]
    [InlineData(21, 59)]
    [InlineData(5, 15)]
    public void REQ_BT_046_a_point_waits_for_the_scrape_after_the_minute_closes(int second, int wait)
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 34, second, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(wait), MetricPoints.UntilNext(now));
    }

    [Fact]
    public async Task REQ_BT_046_the_prometheus_check_passes_when_it_answers()
    {
        await Series(Prometheus()).Ready(CancellationToken.None);
    }

    [Fact]
    public async Task REQ_BT_046_the_prometheus_check_fails_when_it_is_down()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => Series((_, _) => throw new HttpRequestException("refused")).Ready(CancellationToken.None));
    }

    private static async Task<HttpResponseMessage> Http(string url, string? cookie, Func<string, CancellationToken, Task<string>> prometheus)
    {
        await using var host = TestHost.Create();
        var limits = SocketLimits.Standard(null);
        MetricRoutes.Map(host, new SessionReader(PublicKey), Series(prometheus), ProbeLimits.Standard(limits));
        await host.StartAsync();
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(TestHost.Url(host)), url));
        if (cookie is not null) request.Headers.Add("Cookie", $"bt_session={cookie}");
        var answer = await http.SendAsync(request);
        await answer.Content.LoadIntoBufferAsync();
        await host.StopAsync();
        return answer;
    }

    [Fact]
    public async Task F14_anonymous_gets_401_and_prometheus_is_never_asked()
    {
        var asked = new List<string>();

        using var answer = await Http("/metrics/series?bucket=1m", null, Prometheus(asked: asked));

        Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task F14_a_session_without_ops_admin_gets_401()
    {
        using var answer = await Http("/metrics/series?bucket=1m", Token(["batch.read"]), Prometheus());

        Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
    }

    [Fact]
    public async Task F14_an_admin_fetch_passes_with_no_origin_as_a_same_origin_get_sends_none()
    {
        using var answer = await Http("/metrics/series?bucket=1m", Token([SessionReader.OpsAdmin]), Prometheus());

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var body = JsonSerializer.Deserialize<Reply>(await answer.Content.ReadAsStringAsync(), Web)!;
        Assert.Equal("1m", body.Bucket);
        Assert.True(body.Available);
        Assert.Equal(14, body.Series.Length);
    }

    [Theory]
    [InlineData("?bucket=2m")]
    [InlineData("")]
    public async Task F15_any_other_bucket_answers_400(string query)
    {
        using var answer = await Http("/metrics/series" + query, Token([SessionReader.OpsAdmin]), Prometheus());

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
    }

    [Fact]
    public async Task F15_anonymous_with_a_bad_bucket_still_gets_401()
    {
        using var answer = await Http("/metrics/series?bucket=2m", null, Prometheus());

        Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_046_prometheus_down_answers_503()
    {
        using var answer = await Http("/metrics/series?bucket=1m", Token([SessionReader.OpsAdmin]), (_, _) => throw new HttpRequestException("refused"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, answer.StatusCode);
    }
}
