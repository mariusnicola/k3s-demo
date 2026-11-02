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

public class LineSocketTests(Stack stack) : LineSocketTest(stack)
{
    [Fact]
    public async Task REQ_BT_031_Connect_starts_with_a_snapshot_of_the_contract_fields()
    {
        using var socket = await OpenAsync(NewLine());

        var frame = await Sockets.ReceiveAsync(socket);

        Assert.Equal("snapshot", frame.GetProperty("type").GetString());
        Assert.Equal(
            ["at", "entries", "entriesDropped", "evicted", "status", "type"],
            frame.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.False(frame.GetProperty("status").GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task REQ_BT_031_Snapshot_holds_what_already_happened()
    {
        var line = NewLine(new LineOptions { MachineToken = "wrong" });
        await RunOneAsync(line);

        using var socket = await OpenAsync(line);
        var frame = await Sockets.ReceiveAsync(socket);

        Assert.Equal(2, frame.GetProperty("entries").GetArrayLength());
        Assert.Equal("login-failure", frame.GetProperty("entries")[0].GetProperty("reason").GetString());
        Assert.Equal(1, frame.GetProperty("status").GetProperty("errors").GetInt64());
    }

    [Fact]
    public async Task REQ_BT_031_New_entries_arrive_newest_first_once_each()
    {
        var line = NewLine(new LineOptions { MachineToken = "wrong" });
        using var socket = await OpenAsync(line);
        await Sockets.ReceiveAsync(socket);

        await RunOneAsync(line);

        var frame = await Sockets.ReceiveTypeAsync(socket, "line");
        Assert.Equal(["login-failure", "started"], frame.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("reason").GetString()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sockets.ReceiveAsync(socket, TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public async Task REQ_BT_031_Status_change_alone_sends_a_frame()
    {
        var line = NewLine();
        using var socket = await OpenAsync(line);
        await Sockets.ReceiveAsync(socket);

        line.Services.GetRequiredService<LineEngine>().Start(0);

        var frame = await Sockets.ReceiveTypeAsync(socket, "line");
        Assert.True(frame.GetProperty("status").GetProperty("running").GetBoolean());
        Assert.Equal(0, frame.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task REQ_BT_031_Frames_never_exceed_one_a_second()
    {
        var line = NewLine(new LineOptions { MachineToken = "wrong" });
        using var socket = await OpenAsync(line);
        await Sockets.ReceiveAsync(socket);

        var writing = Task.Run(async () =>
        {
            for (var i = 1; i <= 30; i++)
            {
                await RunOneAsync(line, i);
                await Task.Delay(100);
            }
        });
        var first = await Sockets.ReceiveTypeAsync(socket, "line");
        var second = await Sockets.ReceiveTypeAsync(socket, "line");
        await writing;

        var gap = second.GetProperty("at").GetDateTimeOffset() - first.GetProperty("at").GetDateTimeOffset();
        Assert.True(gap >= TimeSpan.FromSeconds(0.9), $"frames {gap.TotalMilliseconds} ms apart");
    }

    [Fact]
    public async Task REQ_BT_031_Line_frame_caps_entries_and_counts_the_rest()
    {
        var line = NewLine();
        var log = line.Services.GetRequiredService<LineLog>();
        using var socket = await OpenAsync(line);
        await Sockets.ReceiveAsync(socket);

        for (var i = 0; i < LineFeed.MaxEntries + 40; i++)
            log.Add(new LineEntry(DateTimeOffset.UtcNow, LineLevel.Info, LineReason.Started, "m", i, null, "farm-a", "Create", 0, null, null, null, null));

        var frame = await Sockets.ReceiveTypeAsync(socket, "line");
        Assert.Equal(LineFeed.MaxEntries, frame.GetProperty("entries").GetArrayLength());
        Assert.Equal(40, frame.GetProperty("entriesDropped").GetInt32());
        Assert.Equal(LineFeed.MaxEntries + 39, frame.GetProperty("entries")[0].GetProperty("batchNumber").GetInt64());
    }

    [Fact]
    public async Task REQ_BT_031_Idle_socket_sends_nothing()
    {
        using var socket = await OpenAsync(NewLine());
        await Sockets.ReceiveAsync(socket);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sockets.ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task REQ_BT_031_Two_clients_each_get_the_frame()
    {
        var line = NewLine(new LineOptions { MachineToken = "wrong" });
        using var one = await OpenAsync(line);
        using var two = await OpenAsync(line);
        await Sockets.ReceiveAsync(one);
        await Sockets.ReceiveAsync(two);

        await RunOneAsync(line);

        Assert.Equal(2, (await Sockets.ReceiveTypeAsync(one, "line")).GetProperty("entries").GetArrayLength());
        Assert.Equal(2, (await Sockets.ReceiveTypeAsync(two, "line")).GetProperty("entries").GetArrayLength());
    }
}
