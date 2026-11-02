using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Xunit;

public sealed partial class MonitorSocketTests
{
    private static readonly Func<string, CancellationToken, Task<string>> OneDose = (path, _) => Task.FromResult(
        path.Contains("bt_dose1_total") ? "{\"status\":\"success\",\"data\":{\"result\":[{\"value\":[1,\"3\"]}]}}" : "{\"status\":\"success\",\"data\":{\"result\":[]}}");

    private static readonly DateTimeOffset PointNow = new(2026, 10, 31, 12, 35, 20, TimeSpan.Zero);

    [Fact]
    public async Task REQ_BT_046_an_admin_socket_gets_the_minute_that_closed_as_the_web_reads_it()
    {
        using var socket = await Open(Token([OpsAdmin]));
        for (var i = 0; i < 3; i++) await Next(socket);

        await new MetricPoints(new MetricSeries(OneDose), hub).Publish(PointNow, CancellationToken.None);

        using var frame = JsonDocument.Parse(await Next(socket));
        Assert.Equal("metricPoint", frame.RootElement.GetProperty("type").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 12, 34, 0, TimeSpan.Zero), frame.RootElement.GetProperty("at").GetDateTimeOffset());
        Assert.Equal(3, frame.RootElement.GetProperty("values").GetProperty("bt_dose1_total").GetDouble());
    }

    [Fact]
    public async Task REQ_BT_046_a_visitor_never_gets_a_metric_point()
    {
        using var socket = await Open();
        Assert.Equal("operations", KindOf(await Next(socket)));

        await new MetricPoints(new MetricSeries(OneDose), hub).Publish(PointNow, CancellationToken.None);
        hub.BroadcastPublic(operations.Frame());

        Assert.Equal("operations", KindOf(await Next(socket)));
    }

    [Fact]
    public async Task REQ_BT_046_prometheus_down_sends_no_frame_and_no_zeros()
    {
        using var socket = await Open(Token([OpsAdmin]));
        for (var i = 0; i < 3; i++) await Next(socket);

        await new MetricPoints(new MetricSeries((_, _) => throw new HttpRequestException("refused")), hub).Publish(PointNow, CancellationToken.None);
        hub.Broadcast(operations.Frame());

        Assert.Equal("operations", KindOf(await Next(socket)));
    }

    [Fact]
    public async Task TM_2264_anonymous_with_no_origin_gets_no_report_frame()
    {
        using var socket = await Open(origin: null);

        await AssertOperationsOnly(socket);
    }

    [Fact]
    public async Task TM_2266_anonymous_with_the_ingress_origin_gets_operations_frames_only()
    {
        using var socket = await Open();

        await AssertOperationsOnly(socket);
    }

    [Theory]
    [InlineData("viewer", "batch.read")]
    [InlineData("olivia", "batch.read,batch.write")]
    [InlineData("ian", "batch.read,batch.inspect")]
    public async Task TM_2268_a_signed_in_user_without_ops_admin_gets_no_report_frame(string user, string permissions)
    {
        using var socket = await Open(Token(permissions.Split(','), subject: user));

        await AssertOperationsOnly(socket);
    }

    [Fact]
    public async Task TM_2269_ada_gets_the_report_the_kept_changes_and_every_later_frame()
    {
        using var socket = await Open(Token([OpsAdmin, "batch.read"]));

        Assert.Equal("report", KindOf(await Next(socket)));
        Assert.Equal("changes", KindOf(await Next(socket)));
        Assert.Equal("operations", KindOf(await Next(socket)));
        hub.Broadcast(report);
        Assert.Equal("report", KindOf(await Next(socket)));
        feed.Add("check", "later", "healthy", "failing");
        Assert.Equal("change", KindOf(await Next(socket)));
        hub.BroadcastPublic(operations.Frame());
        Assert.Equal("operations", KindOf(await Next(socket)));
    }

    public static TheoryData<string, Func<string>> Refused => new()
    {
        { "expired past the skew", () => Token([OpsAdmin], TimeSpan.FromMinutes(-5)) },
        { "wrong issuer", () => Token([OpsAdmin], issuer: "someone-else") },
        { "wrong audience", () => Token([OpsAdmin], audience: "someone-else") },
        { "other key", () => Token([OpsAdmin], credentials: new SigningCredentials(new ECDsaSecurityKey(ECDsa.Create(ECCurve.NamedCurves.nistP256)), SecurityAlgorithms.EcdsaSha256)) },
        { "other algorithm", () => Token([OpsAdmin], credentials: new SigningCredentials(new ECDsaSecurityKey(Key), SecurityAlgorithms.EcdsaSha384)) },
        { "no subject", () => Token([OpsAdmin], subject: "") },
        { "not yet valid", () => Token([OpsAdmin], notBefore: DateTime.UtcNow.AddMinutes(5)) },
        { "garbage", () => "not.a.token" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_cookie_that_fails_validation_opens_an_anonymous_socket(string _, Func<string> token)
    {
        using var socket = await Open(token());

        Assert.Equal(HttpStatusCode.SwitchingProtocols, socket.HttpStatusCode);
        await AssertOperationsOnly(socket);
    }

    [Fact]
    public async Task A_token_in_the_url_gives_nothing()
    {
        var token = Token([OpsAdmin]);

        using var socket = await Open(query: $"?token={token}&bt_session={token}&access_token={token}");

        await AssertOperationsOnly(socket);
    }

    [Fact]
    public async Task A_bearer_header_gives_nothing_because_only_the_cookie_counts()
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", IngressOrigin);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + Token([OpsAdmin]));
        await socket.ConnectAsync(new Uri(socketUrl), Limit());

        await AssertOperationsOnly(socket);
    }

    [Fact]
    public async Task A_cookie_with_no_origin_counts_as_anonymous_because_a_browser_always_sends_one()
    {
        using var socket = await Open(Token([OpsAdmin]), origin: null);

        await AssertOperationsOnly(socket);
    }

    [Fact]
    public async Task Another_origin_is_refused_even_with_a_valid_session()
    {
        var failure = await Assert.ThrowsAsync<WebSocketException>(() => Open(Token([OpsAdmin]), origin: "http://evil.test"));

        Assert.Contains("403", failure.Message);
    }

    [Fact]
    public async Task An_admin_session_that_expires_while_connected_closes_4401_and_gets_no_more_reports()
    {
        using var socket = await Open(Token([OpsAdmin], TimeSpan.FromSeconds(2)));
        Assert.Equal("report", KindOf(await Next(socket)));
        Assert.Equal("changes", KindOf(await Next(socket)));
        Assert.Equal("operations", KindOf(await Next(socket)));

        var buffer = new byte[4096];
        var close = await socket.ReceiveAsync(buffer, Limit());

        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal((WebSocketCloseStatus)4401, socket.CloseStatus);
        Assert.Equal("unauthorized", socket.CloseStatusDescription);
    }

    [Fact]
    public async Task B_046_an_admin_token_past_expiry_inside_the_skew_opens_as_a_visitor_with_no_close()
    {
        using var socket = await Open(Token([OpsAdmin], TimeSpan.FromSeconds(-5)));

        await AssertOperationsOnly(socket);
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task A_viewer_session_that_expires_while_connected_stays_open_for_the_banner()
    {
        using var socket = await Open(Token(["batch.read"], TimeSpan.FromSeconds(1)));
        Assert.Equal("operations", KindOf(await Next(socket)));

        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Equal(WebSocketState.Open, socket.State);
        hub.BroadcastPublic(operations.Frame());
        Assert.Equal("operations", KindOf(await Next(socket)));
    }

    [Fact]
    public void A_public_key_that_does_not_load_stops_the_start_and_names_the_setting()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => new SessionReader("not base64"));

        Assert.Contains("JWT_PUBLIC_KEY", failure.Message);
    }

    [Fact]
    public void The_monitor_checks_a_session_with_the_same_values_as_the_app()
    {
        var folder = Path.Combine(Root(), "app", "src", "BatchTracker.Infrastructure", "Auth");
        var auth = string.Concat(new[] { "Tokens.cs", "Session.cs", "Auth.cs" }.Select(file => File.ReadAllText(Path.Combine(folder, file))));
        var permissions = File.ReadAllText(Path.Combine(Root(), "app", "src", "BatchTracker.Domain", "Permissions.cs"));

        Assert.Contains($"public const string Issuer = \"{SessionReader.Issuer}\";", auth);
        Assert.Contains($"public const string PermissionClaim = \"{SessionReader.PermissionClaim}\";", auth);
        Assert.Contains($"public const string Name = \"{BrowserPaths.SessionCookie}\";", auth);
        Assert.Contains($"public const string OpsAdmin = \"{SessionReader.OpsAdmin}\";", permissions);
        Assert.Contains($"ClockSkew = TimeSpan.FromSeconds({SessionReader.ClockSkew.TotalSeconds})", auth);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));
}
