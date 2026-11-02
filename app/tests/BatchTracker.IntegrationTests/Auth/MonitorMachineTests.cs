using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BatchTracker.IntegrationTests;

public class MonitorMachineTests(Stack stack) : StackTest(stack)
{
    /// <summary>Sends the machine header the way a service does, against the shared API host.</summary>
    private async Task<HttpResponseMessage> SignInAsync(string? header)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/machine");
        if (header is not null) request.Headers.Add(MachineCredential.Header, header);
        return await Stack.Api.CreateClient().SendAsync(request);
    }

    /// <summary>The token a machine holds after a good sign-in.</summary>
    private async Task<string> TokenAsync(string header)
    {
        var reply = await SignInAsync(header);
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var body = await reply.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    /// <summary>A client of the given host that presents the token as a bearer.</summary>
    private static HttpClient Bearer<T>(WebApplicationFactory<T> host, string token) where T : class
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Who the API says a token belongs to.</summary>
    private async Task<JsonElement> WhoAsync(string token) =>
        await Bearer(Stack.Api, token).GetFromJsonAsync<JsonElement>("/api/me");

    [Fact]
    public async Task REQ_BT_047_The_monitor_token_signs_in_as_monitor_with_nothing_granted()
    {
        var me = await WhoAsync(await TokenAsync(Env.MonitorMachineToken));

        Assert.Equal("monitor", me.GetProperty("user").GetString());
        Assert.Empty(me.GetProperty("permissions").EnumerateArray());
        Assert.Empty(me.GetProperty("farms").EnumerateArray());
    }

    [Fact]
    public async Task REQ_BT_047_The_line_token_still_signs_in_as_line()
    {
        var me = await WhoAsync(await TokenAsync(Env.LineMachineToken));

        Assert.Equal("line", me.GetProperty("user").GetString());
        Assert.Contains(me.GetProperty("permissions").EnumerateArray(), p => p.GetString() == Permissions.BatchWrite);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    public async Task REQ_BT_047_Anything_else_is_401(string? header)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(header)).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_The_monitor_cannot_read_or_write_batches()
    {
        var client = Bearer(Stack.Api, await TokenAsync(Env.MonitorMachineToken));

        var list = await client.GetAsync("/api/batches");
        var create = await client.PostAsJsonAsync("/api/batches", new { farmId = "farm-a", product = "Vaccine" });

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_The_monitor_gets_403_from_the_line()
    {
        await using var line = new LineHost(Stack.Api);

        var reply = await Bearer(line, await TokenAsync(Env.MonitorMachineToken)).GetAsync("/line");

        Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode);
    }

    [Fact]
    public async Task REQ_BT_047_The_monitor_upgrades_on_the_hop_socket_and_sees_no_farm()
    {
        await using var live = new LiveHost();
        var token = await TokenAsync(Env.MonitorMachineToken);
        live.CreateClient();
        var hop = new Hop("trace", "batch", "farm-a", HopNames.Api, "Created", DateTimeOffset.UtcNow, true);
        await live.CreateClient().Internal().PostAsJsonAsync("/hops", hop, JsonDefaults.Options);

        using var socket = await Sockets.OpenAsync(live.Server, "/ws/hops", null, bearer: token);

        var snapshot = await Sockets.ReceiveTypeAsync(socket, "snapshot");
        Assert.Empty(snapshot.GetProperty("hops").EnumerateArray());
    }
}
