using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

/// <summary>The running monitor socket and the clients the socket tests open on it.</summary>
public sealed partial class MonitorSocketTests : IAsyncLifetime
{
    private const string OpsAdmin = "ops.admin";
    private const string IngressOrigin = "http://ingress.test";

    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string PublicKey = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SocketHub hub = new(Json);
    private readonly Report report = new(DateTimeOffset.UtcNow, true, [], [new NodeRow("node", "worker", true, 1, 1, 1)], [], [], []);
    private ChangeFeed feed = null!;
    private Operations operations = null!;
    private WebApplication app = null!;
    private string socketUrl = "";

    public async Task InitializeAsync()
    {
        feed = new ChangeFeed(hub);
        feed.Add("check", "kept", "healthy", "failing");
        operations = new Operations(hub);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();
        MonitorSocket.Use(app, IngressOrigin, new SessionReader(PublicKey), SocketLimits.Standard(null), hub, () => report, feed, operations);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        socketUrl = address.Replace("http://", "ws://") + "/ws";
    }

    public async Task DisposeAsync() => await app.DisposeAsync();

    private static string Token(
        string[] permissions, TimeSpan? lifetime = null, string issuer = "batch-tracker", string audience = "batch-tracker",
        string subject = "ada", SigningCredentials? credentials = null, DateTime? notBefore = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>();
        if (subject.Length > 0) claims.Add(new Claim(JwtRegisteredClaimNames.Sub, subject));
        claims.AddRange(permissions.Select(p => new Claim("perm", p)));
        var expires = now + (lifetime ?? TimeSpan.FromHours(8));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = notBefore ?? (expires < now ? expires.AddMinutes(-1) : now),
            IssuedAt = notBefore ?? (expires < now ? expires.AddMinutes(-1) : now),
            Expires = expires,
            SigningCredentials = credentials ?? new SigningCredentials(new ECDsaSecurityKey(Key), SecurityAlgorithms.EcdsaSha256)
        });
    }

    private async Task<ClientWebSocket> Open(string? cookie = null, string? origin = IngressOrigin, string query = "")
    {
        var socket = new ClientWebSocket();
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        if (cookie is not null) socket.Options.SetRequestHeader("Cookie", $"bt_session={cookie}");
        socket.Options.CollectHttpResponseDetails = true;
        await socket.ConnectAsync(new Uri(socketUrl + query), Limit());
        return socket;
    }

    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static async Task<string> Next(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var part = await socket.ReceiveAsync(buffer, Limit());
        Assert.NotEqual(WebSocketMessageType.Close, part.MessageType);
        return Encoding.UTF8.GetString(buffer, 0, part.Count);
    }

    private static string KindOf(string frame)
    {
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.TryGetProperty("type", out var type) ? type.GetString()! : "report";
    }

    /// <summary>The operations frame after the report and the change shows nothing was queued ahead of it.</summary>
    private async Task AssertOperationsOnly(ClientWebSocket socket)
    {
        Assert.Equal("operations", KindOf(await Next(socket)));
        hub.Broadcast(report);
        feed.Add("check", "later", "healthy", "failing");
        hub.BroadcastPublic(operations.Frame());
        Assert.Equal("operations", KindOf(await Next(socket)));
    }
}
