using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

public class RateLimitTests(Stack stack) : StackTest(stack)
{
    private WebApplicationFactory<BatchTracker.Api.ApiMarker> Limited() =>
        Stack.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<RateRules>().AddSingleton(new RateRules())));

    private static HttpClient Client<T>(WebApplicationFactory<T> host, string? user = null, string address = "203.0.113.1") where T : class
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", address);
        if (user is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Sockets.TokenFor(user));
        return client;
    }

    private static async Task<List<HttpResponseMessage>> BurstAsync(HttpClient client, string path, int count) =>
        (await Task.WhenAll(Enumerable.Range(0, count).Select(_ => client.GetAsync(path)))).ToList();

    private static HttpResponseMessage Refused(IEnumerable<HttpResponseMessage> replies) =>
        replies.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);

    [Fact]
    public async Task REQ_BT_050_Under_the_limit_nothing_changes()
    {
        var replies = await BurstAsync(Client(Limited(), "viewer"), "/api/me", 20);

        Assert.All(replies, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public async Task REQ_BT_050_Over_the_limit_per_user_is_429_with_retry_after_and_a_problem()
    {
        var replies = await BurstAsync(Client(Limited(), "viewer"), "/api/me", 400);

        var refused = Refused(replies);
        var seconds = int.Parse(refused.Headers.GetValues("Retry-After").Single());
        var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.True(seconds > 0);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.Contains("wait", problem.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("viewer", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task REQ_BT_050_Over_the_limit_per_address_is_429_with_no_session()
    {
        var replies = await BurstAsync(Client(Limited()), "/api/me", 60);

        Assert.Equal(HttpStatusCode.Unauthorized, replies.First(r => r.StatusCode != HttpStatusCode.TooManyRequests).StatusCode);
        Assert.True(Refused(replies).Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task REQ_BT_050_One_user_over_the_limit_leaves_another_alone()
    {
        var host = Limited();
        Refused(await BurstAsync(Client(host, "viewer"), "/api/me", 400));

        Assert.Equal(HttpStatusCode.OK, (await Client(host, "olivia").GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_050_One_address_over_the_limit_leaves_another_alone()
    {
        var host = Limited();
        Refused(await BurstAsync(Client(host), "/api/me", 60));

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(host, address: "203.0.113.2").GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_050_A_spoofed_first_entry_does_not_escape_the_limit()
    {
        var host = Limited();
        var replies = await Task.WhenAll(Enumerable.Range(0, 60).Select(i =>
            Client(host, address: $"198.51.100.{i}, 203.0.113.3").GetAsync("/api/me")));

        Refused(replies);
    }

    [Fact]
    public async Task REQ_BT_050_Machine_token_has_its_own_partition_and_leaves_a_browser_user_alone()
    {
        var host = Limited();
        var machine = host.CreateClient();
        var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/machine");
        login.Headers.Add(MachineCredential.Header, Env.LineMachineToken);
        var reply = await machine.SendAsync(login);
        var token = JsonDocument.Parse(await reply.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var replies = await BurstAsync(machine, "/api/me", 300);

        Assert.All(replies, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(HttpStatusCode.OK, (await Client(host, "ada").GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_050_Deep_ready_is_limited_to_one_a_second()
    {
        var replies = await BurstAsync(Client(Limited()), "/api/ready/deep", 4);

        Assert.Equal(1, replies.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(3, replies.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task REQ_BT_050_Limit_clears_after_the_wait()
    {
        var client = Client(Limited(), address: "203.0.113.4");
        var refused = Refused(await BurstAsync(client, "/api/me", 60));

        await Task.Delay(TimeSpan.FromSeconds(int.Parse(refused.Headers.GetValues("Retry-After").Single())));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Theory]
    [InlineData("/line")]
    [InlineData("/line/logs")]
    public async Task REQ_BT_050_Line_routes_are_limited(string path)
    {
        await using var line = new LimitedLine(Stack.Api);

        Refused(await BurstAsync(Client(line, "ada"), path, 400));
    }

    [Fact]
    public async Task REQ_BT_050_Live_upgrades_are_limited_per_address()
    {
        await using var live = new LimitedLive();

        Refused(await BurstAsync(Client(live), "/ws/hops", 60));
    }

    private sealed class LimitedLine(ApiHost api) : WebApplicationFactory<LineMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<RateRules>().AddSingleton(new RateRules());
                s.AddHttpClient<LineApi>(http => http.BaseAddress = new Uri("http://localhost/api/"))
                    .ConfigurePrimaryHttpMessageHandler(() => api.Server.CreateHandler());
            });
        }
    }

    private sealed class LimitedLive : WebApplicationFactory<BatchTracker.Live.LiveMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureTestServices(s => s.RemoveAll<RateRules>().AddSingleton(new RateRules()));
        }
    }
}
