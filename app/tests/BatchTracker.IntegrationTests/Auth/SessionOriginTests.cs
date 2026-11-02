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

/// <summary>A write from the wrong origin never lands, and a read gets no CORS header.</summary>
public class SessionOriginTests(Stack stack) : SessionTest(stack)
{
    [Fact]
    public async Task REQ_BT_047_Bearer_with_no_cookie_and_no_origin_can_write()
    {
        var client = Bare();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", new TokenIssuer(Env.PrivateKey, TimeProvider.System).Issue(DemoUsers.Find("olivia")!));

        var response = await client.PostAsJsonAsync("/api/batches", new { farmId = "farm-a", product = "Vaccine" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Origins))]
    public async Task REQ_BT_047_Create_from_another_origin_is_403_and_nothing_written(string origin)
    {
        var body = JsonContent.Create(new { farmId = "farm-a", product = "Forged" });

        var response = await Bare().SendAsync(Request(HttpMethod.Post, "/api/batches", await SessionOf("olivia"), origin, body));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty((await Stack.Store.ListAsync(new BatchFilter(null, null, 10), default)));
    }

    [Theory]
    [InlineData("fail-next")]
    [InlineData("replay")]
    public async Task REQ_BT_047_Bodyless_write_from_another_origin_is_403_and_nothing_written(string action)
    {
        var row = await As("olivia").CreateSettled();
        var path = $"/api/batches/{row.Batch.FarmId}/{row.Batch.Id}/{action}";

        var response = await Bare().SendAsync(Request(HttpMethod.Post, path, await SessionOf("ada"), Evil));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(row.ETag, (await As("ada").Read(row.Batch)).ETag);
    }

    [Fact]
    public async Task REQ_BT_047_Compliance_run_from_another_origin_is_403()
    {
        var response = await Bare().SendAsync(Request(HttpMethod.Post, "/api/compliance/run", await SessionOf("ada"), Evil));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Plain_text_post_from_another_origin_is_403()
    {
        var body = new StringContent("{\"farmId\":\"farm-a\",\"product\":\"Forged\"}", Encoding.UTF8, "text/plain");

        var response = await Bare().SendAsync(Request(HttpMethod.Post, "/api/batches", await SessionOf("olivia"), Evil, body));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Plain_text_post_from_the_right_origin_is_415()
    {
        var body = new StringContent("{\"farmId\":\"farm-a\",\"product\":\"Forged\"}", Encoding.UTF8, "text/plain");

        var response = await Bare().SendAsync(Request(HttpMethod.Post, "/api/batches", await SessionOf("olivia"), Env.Origin, body));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Cookie_write_with_no_origin_is_403()
    {
        var body = JsonContent.Create(new { farmId = "farm-a", product = "Forged" });

        var response = await Bare().SendAsync(Request(HttpMethod.Post, "/api/batches", await SessionOf("olivia"), null, body));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Logout_from_another_origin_is_403_and_the_session_stays()
    {
        var cookie = await SessionOf("viewer");

        var response = await Bare().SendAsync(Request(HttpMethod.Post, "/api/auth/logout", cookie, Evil));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task REQ_BT_047_Sign_in_from_another_origin_is_403_and_sets_no_cookie()
    {
        var body = JsonContent.Create(new { user = "ada" });

        var response = await Bare().SendAsync(Request(HttpMethod.Post, "/api/auth/dev-login", null, Evil, body));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task REQ_BT_047_Same_origin_cookie_write_passes()
    {
        var body = JsonContent.Create(new { farmId = "farm-a", product = "Vaccine" });

        var response = await Bare().SendAsync(Request(HttpMethod.Post, "/api/batches", await SessionOf("olivia"), Env.Origin, body));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Read_from_another_origin_gets_no_cors_header()
    {
        var response = await Bare().SendAsync(Request(HttpMethod.Get, "/api/me", await SessionOf("viewer"), Evil));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }
}
