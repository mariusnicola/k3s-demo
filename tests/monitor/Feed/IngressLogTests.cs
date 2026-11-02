using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

/// <summary>REQ-BT-056: the ingress side of a browser failure, read by correlation id.</summary>
public partial class IngressLogTests
{
    private const string Id = "abc-123";
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string PublicKey = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Line(string id = Id, int status = 502, string path = "/api/batches", string at = "2026-11-01T10:00:00+00:00") =>
        JsonSerializer.Serialize(new { time = at, id, status, path });

    private static string Token(string[] permissions) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "batch-tracker",
        Audience = "batch-tracker",
        Subject = new ClaimsIdentity(new[] { new Claim(JwtRegisteredClaimNames.Sub, "ada") }.Concat(permissions.Select(p => new Claim("perm", p)))),
        Expires = DateTime.UtcNow.AddHours(1),
        SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(Key), SecurityAlgorithms.EcdsaSha256)
    });

    [Fact]
    public void REQ_BT_056_a_line_gives_a_row_with_time_status_and_path()
    {
        var log = new IngressLog(10);

        log.Add(Line());

        var row = Assert.Single(log.Rows(Id));
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T10:00:00+00:00"), row.At);
        Assert.Equal(502, row.Status);
        Assert.Equal("/api/batches", row.Path);
    }

    [Theory]
    [InlineData("198.18.1.1 - - [31/Oct/2026:22:39:54 +0000] \"GET / HTTP/1.1\" 200 1 \"-\" \"-\" 66 0.003 [a] [] b 1 0.003 200 c")]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"time\":\"nope\",\"id\":\"abc\",\"status\":200,\"path\":\"/\"}")]
    [InlineData("{\"id\":\"abc\",\"status\":200,\"path\":\"/\"}")]
    [InlineData("{\"time\":\"2026-11-01T10:00:00+00:00\",\"id\":\"abc\",\"status\":\"x\",\"path\":\"/\"}")]
    [InlineData("{\"time\":\"2026-11-01T10:00:00+00:00\",\"id\":\"abc\",\"status\":200}")]
    public void REQ_BT_056_any_other_line_is_skipped(string line)
    {
        var log = new IngressLog(10);

        log.Add(line);

        Assert.Null(log.Since);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("<script>")]
    public void REQ_BT_056_a_caller_written_id_outside_the_format_is_not_kept(string id)
    {
        var log = new IngressLog(10);

        log.Add(Line(id: id));

        Assert.Null(log.Since);
    }

    [Fact]
    public void REQ_BT_056_an_id_past_the_format_length_is_not_kept()
    {
        var log = new IngressLog(10);

        log.Add(Line(id: new string('a', 65)));

        Assert.Null(log.Since);
    }

    [Fact]
    public void REQ_BT_056_a_query_never_stays_in_a_row()
    {
        var log = new IngressLog(10);

        log.Add(Line(path: "/api/batches?token=abc"));

        Assert.Equal("/api/batches", Assert.Single(log.Rows(Id)).Path);
    }

    [Fact]
    public void REQ_BT_056_a_long_path_is_cut()
    {
        var log = new IngressLog(10);

        log.Add(Line(path: "/" + new string('a', 500)));

        Assert.Equal(IngressLog.MaxPath, Assert.Single(log.Rows(Id)).Path.Length);
    }

    [Fact]
    public void REQ_BT_056_a_very_long_line_is_skipped()
    {
        var log = new IngressLog(10);

        log.Add(Line(path: "/" + new string('a', IngressLog.MaxLine)));

        Assert.Null(log.Since);
    }

    [Fact]
    public void REQ_BT_056_memory_is_bounded_and_since_says_what_was_let_go()
    {
        var log = new IngressLog(2);

        log.Add(Line(id: "one", at: "2026-11-01T10:00:00+00:00"));
        log.Add(Line(id: "two", at: "2026-11-01T10:00:01+00:00"));
        log.Add(Line(id: "three", at: "2026-11-01T10:00:02+00:00"));

        Assert.Empty(log.Rows("one"));
        Assert.Single(log.Rows("three"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T10:00:01+00:00"), log.Since);
    }

    [Fact]
    public void REQ_BT_056_rows_are_newest_first_with_repeats_from_a_reconnect_removed()
    {
        var log = new IngressLog(10);
        var late = Line(status: 200, at: "2026-11-01T10:00:05+00:00");

        log.Add(Line());
        log.Add(late);
        log.Add(late);

        var rows = log.Rows(Id);
        Assert.Equal(2, rows.Length);
        Assert.Equal(200, rows[0].Status);
    }

    private static Stream Lines(params string[] lines) => new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));

    [Fact]
    public async Task REQ_BT_056_following_reads_each_line_and_stops_being_available_when_the_stream_ends()
    {
        var log = new IngressLog(10);
        bool? during = null;

        await log.Follow(_ =>
        {
            during = log.Reading;
            return Task.FromResult(Lines(Line()));
        }, CancellationToken.None);

        Assert.Single(log.Rows(Id));
        Assert.False(during);
        Assert.False(log.Reading);
    }

    [Fact]
    public async Task REQ_BT_056_a_failed_open_leaves_it_unavailable_and_says_why()
    {
        var log = new IngressLog(10);

        await Assert.ThrowsAsync<HttpRequestException>(() => log.Follow(_ => throw new HttpRequestException("no pod"), CancellationToken.None));

        Assert.False(log.Reading);
    }

    [Fact]
    public async Task REQ_BT_056_it_is_available_while_the_stream_is_open()
    {
        var log = new IngressLog(10);
        var seen = new TaskCompletionSource<bool>();
        var writer = new Pipe();

        var following = log.Follow(_ => Task.FromResult(writer.Reader), CancellationToken.None);
        writer.Write(Line() + "\n");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (log.Rows(Id).Length == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        seen.SetResult(log.Reading);
        writer.Close();
        await following;

        Assert.True(await seen.Task);
    }

    private sealed class Pipe
    {
        private readonly System.IO.Pipes.AnonymousPipeServerStream server = new(System.IO.Pipes.PipeDirection.Out);
        public Stream Reader { get; }
        public Pipe() => Reader = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, server.ClientSafePipeHandle);
        public void Write(string text) { server.Write(Encoding.UTF8.GetBytes(text)); server.Flush(); }
        public void Close() => server.Dispose();
    }

    [Fact]
    public async Task REQ_BT_056_after_a_row_a_reconnect_asks_only_for_the_gap()
    {
        var log = new IngressLog(10);
        var asked = new List<int?>();
        Task<Stream> Open(int? since) { asked.Add(since); return Task.FromResult(Lines(Line(at: DateTimeOffset.UtcNow.AddSeconds(-30).ToString("O")))); }

        await log.Follow(Open, CancellationToken.None);
        await log.Follow(Open, CancellationToken.None);

        Assert.Null(asked[0]);
        Assert.InRange(asked[1]!.Value, 30, 40);
    }
}
