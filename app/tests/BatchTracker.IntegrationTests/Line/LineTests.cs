using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Application.Dispatching;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatchTracker.IntegrationTests;

public sealed class LineTests(Stack stack) : StackTest(stack)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private LineHost line = null!;
    private DispatcherHost dispatcher = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        line = new LineHost(Stack.Api);
        dispatcher = new DispatcherHost(services =>
        {
            services.RemoveAll<IWorkerClient>().AddSingleton(_ => Stack.Worker.NewClient());
            services.RemoveAll<DispatcherOptions>().AddSingleton(new DispatcherOptions { SweepInterval = TimeSpan.FromMilliseconds(100) });
        });
        dispatcher.CreateClient();
        dispatcher.Leadership.Gain();
    }

    public override async Task DisposeAsync()
    {
        await line.DisposeAsync();
        await dispatcher.DisposeAsync();
    }

    private HttpClient Client(string? user)
    {
        var client = line.CreateClient();
        if (user is not null)
        {
            var token = new TokenIssuer(Env.PrivateKey, TimeProvider.System).Issue(DemoUsers.Find(user)!);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    private async Task<LineStatus> StatusAsync() =>
        (await Client("ada").GetFromJsonAsync<LineStatus>("/line", JsonDefaults.Options))!;

    [Fact]
    public async Task REQ_BT_047_Cross_site_start_and_stop_with_the_cookie_are_403_and_the_line_is_unchanged()
    {
        var client = line.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        var cookie = $"{SessionCookie.Name}={new TokenIssuer(Env.PrivateKey, TimeProvider.System).Issue(DemoUsers.Find("ada")!)}";
        var before = await StatusAsync();

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, "/line");
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add("Origin", "http://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
        }
        Assert.Equal(before.Running, (await StatusAsync()).Running);
    }

    [Fact]
    public async Task REQ_BT_047_Same_origin_cookie_reads_the_line()
    {
        var client = line.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/line");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={new TokenIssuer(Env.PrivateKey, TimeProvider.System).Issue(DemoUsers.Find("ada")!)}");

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    private LineEngine Engine =>
 line.Services.GetRequiredService<LineEngine>();

    private static async Task Until(Func<Task<bool>> condition, string what)
    {
        using var timer = new CancellationTokenSource(Limit);
        while (!await condition())
        {
            if (timer.IsCancellationRequested) throw new TimeoutException(what);
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task REQ_BT_031_Line_walks_a_batch_to_Closed_through_the_api()
    {
        var response = await Client("ada").PostAsJsonAsync("/line", new { rate = 1 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await Until(async () => (await StatusAsync()).Started >= 1, "started");
        (await Client("ada").DeleteAsync("/line")).EnsureSuccessStatusCode();

        await Until(async () => (await StatusAsync()).Closed >= 1, "closed");
        var status = await StatusAsync();
        Assert.False(status.Running);
        Assert.Equal(0, status.Errors);

        var batches = await As("ada").Http.GetFromJsonAsync<List<Batch>>("/api/batches?state=Closed", JsonDefaults.Options);
        var batch = batches!.First();
        Assert.Equal(6, batch.Steps.Count);
        Assert.Equal(DemoUsers.LineMachine.User, batch.Steps[0].By);
        Assert.True(HashChain.Verify(batch));
    }

    [Fact]
    public async Task REQ_BT_031_One_in_ten_fails_inspection()
    {
        line.CreateClient();

        await Engine.RunBatchAsync(10, CancellationToken.None);

        Assert.Equal(1, (await StatusAsync()).Rejected);
        var batch = Assert.Single((await As("ada").Http.GetFromJsonAsync<List<Batch>>("/api/batches?state=Rejected", JsonDefaults.Options))!);
        Assert.False(string.IsNullOrWhiteSpace(batch.Reason));
    }

    [Fact]
    public async Task REQ_BT_031_Rate_defaults_to_one_and_can_change_while_running()
    {
        var client = Client("ada");

        var first = await (await client.PostAsync("/line", JsonContent.Create(new { }))).Content.ReadFromJsonAsync<LineStatus>(JsonDefaults.Options);
        var second = await (await client.PostAsJsonAsync("/line", new { rate = 2.5 })).Content.ReadFromJsonAsync<LineStatus>(JsonDefaults.Options);
        await client.DeleteAsync("/line");

        Assert.Equal(1, first!.Rate);
        Assert.Equal(2.5, second!.Rate);
    }

    [Fact]
    public async Task REQ_BT_031_Rate_zero_creates_nothing()
    {
        var response = await Client("ada").PostAsJsonAsync("/line", new { rate = 0 });
        await Task.Delay(1500);
        await Client("ada").DeleteAsync("/line");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await StatusAsync()).Started);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(-1)]
    [InlineData(35.01)]
    [InlineData(36)]
    [InlineData(100000)]
    public async Task REQ_BT_031_Rate_out_of_range_is_400(double rate)
    {
        var response = await Client("ada").PostAsJsonAsync("/line", new { rate });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await StatusAsync()).Running);
    }

    [Fact]
    public async Task REQ_BT_031_Rate_of_35_is_the_top_of_the_range()
    {
        var response = await Client("ada").PostAsJsonAsync("/line", new { rate = 35 });
        await Client("ada").DeleteAsync("/line");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(35, (await response.Content.ReadFromJsonAsync<LineStatus>(JsonDefaults.Options))!.Rate);
    }

    [Fact]
    public async Task REQ_BT_031_Refused_rate_leaves_a_running_line_at_its_rate()
    {
        var client = Client("ada");
        await client.PostAsJsonAsync("/line", new { rate = 0 });

        var refused = await client.PostAsJsonAsync("/line", new { rate = 36 });
        var status = await StatusAsync();
        await client.DeleteAsync("/line");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.True(status.Running);
        Assert.Equal(0, status.Rate);
    }

    [Fact]
    public async Task REQ_BT_031_Very_low_rate_leaves_the_engine_running()
    {
        var response = await Client("ada").PostAsJsonAsync("/line", new { rate = 1e-12 });
        await Until(async () => (await StatusAsync()).Started >= 1, "first batch");
        await Task.Delay(300);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Engine.ExecuteTask!.IsCompleted, "the pause must not end the engine");
        await Client("ada").DeleteAsync("/line");
    }

    [Fact]
    public async Task REQ_BT_031_Rate_too_large_to_be_finite_is_400()
    {
        var body = new StringContent("{\"rate\":1e400}", System.Text.Encoding.UTF8, "application/json");

        var response = await Client("ada").PostAsync("/line", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await StatusAsync()).Running);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("olivia", HttpStatusCode.Forbidden)]
    public async Task REQ_BT_031_Line_needs_ops_admin(string? user, HttpStatusCode expected)
    {
        var client = Client(user);

        Assert.Equal(expected, (await client.GetAsync("/line")).StatusCode);
        Assert.Equal(expected, (await client.DeleteAsync("/line")).StatusCode);
        Assert.Equal(expected, (await client.PostAsJsonAsync("/line", new { rate = 1 })).StatusCode);
        Assert.False((await StatusAsync()).Running);
    }

    [Fact]
    public async Task REQ_BT_014_Line_healthz_is_anonymous()
    {
        Assert.Equal(HttpStatusCode.OK, (await Client(null).GetAsync("/healthz")).StatusCode);
    }
}

