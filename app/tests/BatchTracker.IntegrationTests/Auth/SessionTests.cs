using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BatchTracker.IntegrationTests;

public class SessionTests(Stack stack) : SessionTest(stack)
{
    [Fact]
    public async Task REQ_BT_047_Sign_in_sets_an_http_only_same_site_strict_cookie()
    {
        var response = await Bare().PostAsJsonAsync("/api/auth/dev-login", new { user = "viewer" });

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.StartsWith($"{SessionCookie.Name}=", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/", cookie);
        Assert.Contains("max-age=28800", cookie);
    }

    [Fact]
    public async Task REQ_BT_047_Sign_in_body_holds_no_token()
    {
        var response = await Bare().PostAsJsonAsync("/api/auth/dev-login", new { user = "viewer" });

        var body = await response.Content.ReadAsStringAsync();
        var value = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0].Split('=')[1];
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(value, body);
        Assert.Equal("viewer", JsonDocument.Parse(body).RootElement.GetProperty("user").GetString());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("", true)]
    public async Task REQ_BT_047_Secure_flag_comes_from_config_and_defaults_on(string setting, bool secure)
    {
        var client = WithSetting(Stack.Api, "COOKIE_SECURE", setting);

        var response = await client.PostAsJsonAsync("/api/auth/dev-login", new { user = "viewer" });

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.Equal(secure, cookie.Contains("secure"));
    }

    [Fact]
    public async Task REQ_BT_047_Cookie_alone_signs_a_request_in()
    {
        var response = await Bare().SendAsync(Request(HttpMethod.Get, "/api/me", await SessionOf("olivia"), null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("olivia", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("user").GetString());
    }

    [Theory]
    [InlineData("not.a.token")]
    [InlineData("")]
    public async Task REQ_BT_047_Garbage_cookie_is_401(string value)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookie($"{SessionCookie.Name}={value}"));
    }

    [Fact]
    public async Task REQ_BT_047_Tampered_cookie_is_401()
    {
        var cookie = await SessionOf("viewer");
        var last = cookie[^2] == 'A' ? 'B' : 'A';

        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookie(cookie[..^2] + last + cookie[^1]));
    }

    [Fact]
    public async Task REQ_BT_047_Cookie_signed_with_a_foreign_key_is_401()
    {
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var foreign = new TokenIssuer(Convert.ToBase64String(other.ExportPkcs8PrivateKey()), TimeProvider.System)
            .Issue(DemoUsers.Find("ada")!);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookie($"{SessionCookie.Name}={foreign}"));
    }

    [Fact]
    public async Task REQ_BT_047_Expired_cookie_is_401()
    {
        var expired = new TokenIssuer(Env.PrivateKey, new TwoDaysAgo()).Issue(DemoUsers.Find("ada")!);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookie($"{SessionCookie.Name}={expired}"));
    }

    [Fact]
    public async Task REQ_BT_047_Bearer_wins_over_a_cookie_and_gives_one_identity()
    {
        var request = Request(HttpMethod.Get, "/api/me", await SessionOf("viewer"), null);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", new TokenIssuer(Env.PrivateKey, TimeProvider.System).Issue(DemoUsers.Find("ada")!));

        var response = await Bare().SendAsync(request);

        Assert.Equal("ada", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("user").GetString());
    }

    [Fact]
    public async Task REQ_BT_047_Bad_bearer_does_not_fall_back_to_the_cookie()
    {
        var request = Request(HttpMethod.Get, "/api/me", await SessionOf("viewer"), null);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.token");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Bare().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Token_in_the_query_never_signs_in()
    {
        var token = new TokenIssuer(Env.PrivateKey, TimeProvider.System).Issue(DemoUsers.Find("viewer")!);

        foreach (var name in new[] { "token", "access_token" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Bare().GetAsync($"/api/me?{name}={token}")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Logout_clears_the_cookie_and_the_next_call_is_401()
    {
        var user = As("viewer");
        Assert.Equal(HttpStatusCode.OK, (await user.Http.GetAsync("/api/me")).StatusCode);

        var response = await user.Http.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cleared = Assert.Single(response.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.StartsWith($"{SessionCookie.Name}=;", cleared);
        Assert.Contains("httponly", cleared);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Http.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Logout_without_a_session_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bare().PostAsync("/api/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Inspection_reason_over_the_limit_is_400_and_nothing_written()
    {
        var olivia = As("olivia");
        var row = await olivia.WalkTo(BatchState.Dose2, As("ian"));

        var response = await As("ian").Inspect(row.Batch, false, new string('x', BatchService.MaxReasonLength + 1), row.ETag);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(row.ETag, (await olivia.Read(row.Batch)).ETag);
    }

    [Fact]
    public async Task REQ_BT_047_Inspection_reason_at_the_limit_is_accepted()
    {
        var olivia = As("olivia");
        var row = await olivia.WalkTo(BatchState.Dose2, As("ian"));

        var response = await As("ian").Inspect(row.Batch, false, new string('x', BatchService.MaxReasonLength), row.ETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
