using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Line;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BatchTracker.IntegrationTests;

/// <summary>Who may open the Line socket, how it ends, and the cap on batches in flight.</summary>
public class LineSocketAccessTests(Stack stack) : LineSocketTest(stack)
{
    [Fact]
    public async Task REQ_BT_031_Anonymous_upgrade_is_401()
    {
        Assert.Contains("401", await Sockets.RefusalAsync(NewLine().Server, Path, null));
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("olivia")]
    [InlineData("ian")]
    public async Task REQ_BT_031_Upgrade_without_ops_admin_is_403(string user)
    {
        Assert.Contains("403", await Sockets.RefusalAsync(NewLine().Server, Path, Sockets.CookieFor(user)));
    }

    [Fact]
    public async Task REQ_BT_047_Line_socket_from_another_origin_is_403()
    {
        Assert.Contains("403", await Sockets.RefusalAsync(NewLine().Server, Path, Sockets.CookieFor("ada"), "http://evil.example"));
    }

    [Fact]
    public async Task REQ_BT_047_Line_socket_never_opens_on_a_url_token()
    {
        var path = $"{Path}?token={Sockets.TokenFor("ada")}";

        Assert.Contains("401", await Sockets.RefusalAsync(NewLine().Server, path, null));
    }

    [Fact]
    public async Task REQ_BT_050_Sixth_line_socket_of_a_user_is_429()
    {
        var line = NewLine();
        var open = new List<WebSocket>();
        for (var i = 0; i < SocketRules.MaxPerUser; i++) open.Add(await OpenAsync(line));

        Assert.Contains("429", await Sockets.RefusalAsync(line.Server, Path, Sockets.CookieFor("ada")));
        foreach (var socket in open) socket.Dispose();
    }

    [Fact]
    public async Task REQ_BT_047_Line_socket_closes_4401_at_token_expiry()
    {
        var line = NewLine();
        using var socket = await Sockets.OpenAsync(line.Server, Path, Sockets.CookieFor("ada", new ExpiresIn(TimeSpan.FromSeconds(2))));
        await Sockets.ReceiveAsync(socket);

        var (status, reason) = await Sockets.WaitForCloseAsync(socket, Limit);

        Assert.Equal(4401, (int?)status);
        Assert.Equal("unauthorized", reason);
    }

    [Fact]
    public void REQ_BT_031_Line_socket_pings_every_30_seconds()
    {
        var options = NewLine().Services.GetRequiredService<IOptions<WebSocketOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(30), options.KeepAliveInterval);
    }

    [Fact]
    public async Task REQ_BT_047_In_flight_batches_stay_under_the_cap_at_any_rate()
    {
        var gate = new HoldCreates();
        var line = NewLine(new LineOptions { MaxInFlight = 3 }, api => { gate.InnerHandler = api; return gate; });
        var engine = line.Services.GetRequiredService<LineEngine>();

        engine.Start(1e9);
        await gate.Reached(3, Limit);
        await Task.Delay(500);
        engine.Stop();

        Assert.Equal(3, gate.Peak);
        Assert.Equal(3, engine.Status().InFlight);
        gate.Release();
    }

    [Fact]
    public async Task REQ_BT_047_A_finished_batch_frees_its_place()
    {
        var gate = new HoldCreates();
        var line = NewLine(new LineOptions { MaxInFlight = 1, EventTimeout = TimeSpan.FromMilliseconds(100) }, api => { gate.InnerHandler = api; return gate; });
        var engine = line.Services.GetRequiredService<LineEngine>();

        engine.Start(100);
        await gate.Reached(1, Limit);
        gate.Release();
        await gate.Reached(2, Limit);
        engine.Stop();
        gate.Release();
    }
}
