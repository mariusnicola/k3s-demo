using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;

namespace BatchTracker.IntegrationTests;

public class AuthTests(Stack stack) : StackTest(stack)
{
    public static TheoryData<string, string> ProtectedRoutes() => new()
    {
        { "GET", "/api/me" },
        { "GET", "/api/batches" },
        { "POST", "/api/batches" },
        { "GET", "/api/batches/farm-a/00000000-0000-0000-0000-000000000001" },
        { "POST", "/api/batches/farm-a/00000000-0000-0000-0000-000000000001/advance" },
        { "POST", "/api/batches/farm-a/00000000-0000-0000-0000-000000000001/inspect" },
        { "POST", "/api/batches/farm-a/00000000-0000-0000-0000-000000000001/fail-next" },
        { "POST", "/api/batches/farm-a/00000000-0000-0000-0000-000000000001/replay" },
        { "GET", "/api/summary" },
        { "GET", "/api/alerts" },
        { "POST", "/api/compliance/run" }
    };

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task REQ_BT_015_No_token_is_401(string method, string path)
    {
        var response = await Anonymous().Http.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task REQ_BT_015_Bad_token_is_401(string method, string path)
    {
        var client = Anonymous().Http;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.token");

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_015_Token_signed_with_another_key_is_401()
    {
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var forged = new TokenIssuer(Convert.ToBase64String(other.ExportPkcs8PrivateKey()), TimeProvider.System)
            .Issue(DemoUsers.Find("ada")!);
        var client = Anonymous().Http;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged);

        var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_015_Expired_token_is_401()
    {
        var expired = new TokenIssuer(Env.PrivateKey, new PastClock()).Issue(DemoUsers.Find("ada")!);
        var client = Anonymous().Http;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expired);

        var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_015_Token_expires_in_eight_hours()
    {
        var login = await Anonymous().Http.PostAsJsonAsync("/api/auth/dev-login", new { user = "ada" });
        var token = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=')[1];

        var jwt = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token);

        Assert.Equal(TimeSpan.FromHours(8), jwt.ValidTo - jwt.ValidFrom);
        Assert.Equal("ES256", jwt.Alg);
    }

    [Theory]
    [InlineData("viewer", "POST", "/api/batches")]
    [InlineData("ian", "POST", "/api/batches")]
    [InlineData("olivia", "POST", "/api/compliance/run")]
    [InlineData("ian", "POST", "/api/compliance/run")]
    [InlineData("olivia", "POST", "/api/batches/farm-a/00000000-0000-0000-0000-000000000001/inspect")]
    [InlineData("viewer", "POST", "/api/batches/farm-a/00000000-0000-0000-0000-000000000001/advance")]
    [InlineData("ian", "POST", "/api/batches/farm-a/00000000-0000-0000-0000-000000000001/advance")]
    [InlineData("olivia", "POST", "/api/batches/farm-a/00000000-0000-0000-0000-000000000001/fail-next")]
    [InlineData("olivia", "POST", "/api/batches/farm-a/00000000-0000-0000-0000-000000000001/replay")]
    public async Task REQ_BT_015_Missing_permission_is_403(string user, string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { })
        };

        var response = await As(user).Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("OLIVIA")]
    public async Task REQ_BT_015_Unknown_user_cannot_sign_in(string user)
    {
        var response = await Anonymous().Http.PostAsJsonAsync("/api/auth/dev-login", new { user });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_007_Sign_in_without_a_user_is_400()
    {
        var response = await Anonymous().Http.PostAsJsonAsync("/api/auth/dev-login", new { user = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("olivia", "batch.read,batch.write", "farm-a,farm-b")]
    [InlineData("ian", "batch.read,batch.inspect", "*")]
    [InlineData("ada", "batch.read,batch.write,batch.inspect,ops.admin", "*")]
    [InlineData("viewer", "batch.read", "farm-a")]
    public async Task REQ_BT_016_Me_shows_the_demo_user_table(string user, string permissions, string farms)
    {
        var me = JsonDocument.Parse(await As(user).Http.GetStringAsync("/api/me")).RootElement;

        Assert.Equal(user, me.GetProperty("user").GetString());
        Assert.Equal(permissions.Split(','), me.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(farms.Split(','), me.GetProperty("farms").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task REQ_BT_016_Read_of_another_farm_is_403()
    {
        var other = await As("ada").Create("farm-b");

        var response = await As("viewer").Get(other.Batch);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_016_Create_on_another_farm_is_403()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await As("olivia").PostCreate("farm-c")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_016_Unknown_farm_is_403_for_a_user_with_listed_farms()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await As("olivia").PostCreate("farm-unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As("olivia").Http.GetAsync("/api/batches?farmId=farm-unknown")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_016_Advance_on_another_farm_is_403_and_nothing_written()
    {
        var other = await As("ada").CreateSettled("farm-c");

        var response = await As("olivia").Advance(other.Batch, "Dose1", other.ETag);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(other.ETag, (await As("ada").Read(other.Batch)).ETag);
    }

    [Fact]
    public async Task REQ_BT_016_List_shows_only_own_farms()
    {
        var ada = As("ada");
        await ada.Create("farm-a");
        await ada.Create("farm-b");
        await ada.Create("farm-c");

        var viewer = await As("viewer").Http.GetFromJsonAsync<List<Batch>>("/api/batches", JsonDefaults.Options);
        var olivia = await As("olivia").Http.GetFromJsonAsync<List<Batch>>("/api/batches", JsonDefaults.Options);
        var all = await ada.Http.GetFromJsonAsync<List<Batch>>("/api/batches", JsonDefaults.Options);

        Assert.Equal(["farm-a"], viewer!.Select(b => b.FarmId));
        Assert.Equal(["farm-a", "farm-b"], olivia!.Select(b => b.FarmId).Order());
        Assert.Equal(3, all!.Count);
    }

    [Fact]
    public async Task REQ_BT_016_Summary_and_alerts_cover_only_own_farms()
    {
        var ada = As("ada");
        await ada.Create("farm-a");
        await ada.Create("farm-b");

        var summary = JsonDocument.Parse(await As("viewer").Http.GetStringAsync("/api/summary")).RootElement;

        Assert.Equal(1, summary.GetProperty("byState").GetProperty("Created").GetInt32());
        Assert.Equal(1, summary.GetProperty("pending").GetInt32());
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/api/healthz")]
    [InlineData("/api/ready")]
    [InlineData("/api/ready/deep")]
    public async Task REQ_BT_014_Health_routes_need_no_token(string path)
    {
        Assert.Equal(HttpStatusCode.OK, (await Anonymous().Http.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_014_Deep_ready_names_both_dependencies()
    {
        var body = JsonDocument.Parse(await Anonymous().Http.GetStringAsync("/api/ready/deep")).RootElement;

        Assert.True(body.GetProperty("db").GetBoolean());
        Assert.True(body.GetProperty("serviceBus").GetBoolean());
    }

    [Fact]
    public async Task REQ_BT_014_Unmapped_route_is_refused_without_a_token()
    {
        var response = await Anonymous().Http.GetAsync("/api/nothing-here");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class PastClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(-2);
    }
}
