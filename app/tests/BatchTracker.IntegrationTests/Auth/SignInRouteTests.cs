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

/// <summary>The routes that sign a person or a machine in.</summary>
public class SignInRouteTests(Stack stack) : SessionTest(stack)
{
    [Theory]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("yes")]
    public async Task REQ_BT_047_Dev_login_is_404_unless_switched_on(string setting)
    {
        var client = WithSetting(Stack.Api, "DEV_LOGIN_ENABLED", setting);

        var response = await client.PostAsJsonAsync("/api/auth/dev-login", new { user = "ada" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task REQ_BT_047_Dev_login_with_an_expired_header_still_signs_in()
    {
        var client = Bare();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", new TokenIssuer(Env.PrivateKey, new TwoDaysAgo()).Issue(DemoUsers.Find("ada")!));

        var response = await client.PostAsJsonAsync("/api/auth/dev-login", new { user = "viewer" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"user\":1}", "application/json")]
    [InlineData("{\"user\":null}", "application/json")]
    [InlineData("{}", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("{\"user\":\"ada\"}", "text/plain")]
    public async Task REQ_BT_047_Malformed_sign_in_is_a_client_error_never_500(string body, string type)
    {
        var response = await Bare().PostAsync("/api/auth/dev-login", new StringContent(body, Encoding.UTF8, type));

        Assert.True((int)response.StatusCode is 400 or 415, $"got {(int)response.StatusCode}");
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    public async Task REQ_BT_047_Machine_route_refuses_a_wrong_credential(string? value)
    {
        var response = await PostMachine(Bare(), value);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("token", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task REQ_BT_047_Machine_credential_works_with_dev_login_off_and_sets_no_cookie()
    {
        var client = WithSetting(Stack.Api, "DEV_LOGIN_ENABLED", "false");

        var response = await PostMachine(client, Env.LineMachineToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task REQ_BT_047_Machine_token_walks_batches_and_cannot_administer()
    {
        var login = await PostMachine(Bare(), Env.LineMachineToken);
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        var client = Bare();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var created = await client.PostAsJsonAsync("/api/batches", new { farmId = "farm-c", product = "Vaccine" });
        var admin = await client.PostAsync("/api/compliance/run", null);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_Nobody_signs_in_as_the_machine()
    {
        var response = await Bare().PostAsJsonAsync("/api/auth/dev-login", new { user = DemoUsers.LineMachine.User });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void REQ_BT_047_Live_and_line_run_without_the_private_key_and_hold_no_issuer()
    {
        Environment.SetEnvironmentVariable("JWT_PRIVATE_KEY", null);
        try
        {
            using var live = new LiveHost();
            using var line = new LineHost(Stack.Api);
            Assert.Null(live.Services.GetService(typeof(TokenIssuer)));
            Assert.Null(line.Services.GetService(typeof(TokenIssuer)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("JWT_PRIVATE_KEY", Env.PrivateKey);
        }
    }

    [Fact]
    public async Task B_045_Sign_in_that_is_off_says_so_in_words_that_fit_sign_in()
    {
        var client = WithSetting(Stack.Api, "DEV_LOGIN_ENABLED", "false");

        var reply = await client.PostAsJsonAsync("/api/auth/dev-login", new { user = "ada" });

        Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
        var body = JsonDocument.Parse(await reply.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(ProblemWords.SignInOff, body.GetProperty("detail").GetString());
        Assert.Equal(ProblemWords.SignInOffNext, body.GetProperty("next").GetString());
    }
}
