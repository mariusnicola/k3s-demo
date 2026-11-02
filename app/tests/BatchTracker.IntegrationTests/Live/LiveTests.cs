using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using BatchTracker.Application;
using BatchTracker.Domain;
using BatchTracker.Infrastructure;
using BatchTracker.Live;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BatchTracker.IntegrationTests;

public class LiveTests(Stack stack) : LiveTest(stack)
{
    [Fact]
    public async Task REQ_BT_028_Connect_starts_with_one_snapshot_of_the_contract_fields()
    {
        using var socket = await OpenAsync("ada");

        var frame = await Sockets.ReceiveAsync(socket);

        Assert.Equal("snapshot", frame.GetProperty("type").GetString());
        Assert.Equal(
            ["alerts", "at", "batches", "batchesDropped", "capped", "hops", "reconciledAt", "summary", "type"],
            frame.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task REQ_BT_028_Posted_hop_is_broadcast_on_the_socket()
    {
        using var socket = await ConnectedAsync("ada");

        await PostAsync(NewHop("farm-a", "trace-1"));

        var frame = await Sockets.ReceiveTypeAsync(socket, "hops");
        Assert.Equal("trace-1", frame.GetProperty("hops")[0].GetProperty("traceId").GetString());
        Assert.Equal("api", frame.GetProperty("hops")[0].GetProperty("hop").GetString());
    }

    [Fact]
    public async Task REQ_BT_028_Reporter_reaches_live_over_http()
    {
        using var socket = await ConnectedAsync("ada");

        var reports = new ReportBuffer();
        reports.Report(NewHop("farm-a", "trace-2"));
        await new ReportFlusher(live.CreateClient().Internal(), reports).FlushAsync(default);

        Assert.Equal("trace-2", (await Sockets.ReceiveTypeAsync(socket, "hops")).GetProperty("hops")[0].GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task REQ_BT_016_Socket_sends_only_the_users_farms()
    {
        using var socket = await ConnectedAsync("viewer");

        await PostAsync(NewHop("farm-b", "hidden"));
        await PostAsync(NewHop("farm-a", "shown"));

        var frame = await Sockets.ReceiveTypeAsync(socket, "hops");
        Assert.Equal(["shown"], frame.GetProperty("hops").EnumerateArray().Select(h => h.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task REQ_BT_016_Batch_frame_of_another_farm_never_reaches_the_socket()
    {
        using var socket = await ConnectedAsync("viewer");

        await PostChangeAsync(Row("farm-b"));
        var shown = Row("farm-a");
        await PostChangeAsync(shown);

        var frame = await Sockets.ReceiveTypeAsync(socket, "batch");
        Assert.Equal(shown.Batch.Id, frame.GetProperty("batch").GetProperty("id").GetString());
    }

    [Fact]
    public async Task REQ_BT_028_Batch_frame_carries_the_etag_and_the_full_row()
    {
        using var socket = await ConnectedAsync("ada");
        var row = Row("farm-c", 3, BatchState.Dose1);

        await PostChangeAsync(row);

        var frame = await Sockets.ReceiveTypeAsync(socket, "batch");
        Assert.Equal("\"etag-3\"", frame.GetProperty("etag").GetString());
        Assert.Equal(3, frame.GetProperty("batch").GetProperty("version").GetInt64());
        Assert.Equal("Dose1", frame.GetProperty("batch").GetProperty("state").GetString());
    }

    [Fact]
    public async Task REQ_BT_028_Snapshot_holds_rows_read_from_the_store_for_own_farms_only()
    {
        var ada = As("ada");
        var mine = await ada.Create("farm-a");
        await ada.Create("farm-b");

        await WaitForFirstScanAsync();
        using var socket = await OpenAsync("viewer");

        var frame = await Sockets.ReceiveAsync(socket);
        var row = Assert.Single(frame.GetProperty("batches").EnumerateArray());
        Assert.Equal(mine.Batch.Id, row.GetProperty("batch").GetProperty("id").GetString());
        Assert.False(string.IsNullOrEmpty(row.GetProperty("etag").GetString()));
        Assert.Equal(1, frame.GetProperty("summary").GetProperty("byState").GetProperty("Created").GetInt32());
        Assert.Equal(0, frame.GetProperty("batchesDropped").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, frame.GetProperty("reconciledAt").ValueKind);
    }

    [Fact]
    public async Task REQ_BT_028_Etag_from_a_snapshot_is_good_for_if_match()
    {
        var olivia = As("olivia");
        var row = await olivia.CreateSettled();

        await WaitForFirstScanAsync();
        using var socket = await OpenAsync("olivia");
        var frame = await Sockets.ReceiveAsync(socket);

        var etag = frame.GetProperty("batches")[0].GetProperty("etag").GetString();
        Assert.Equal(HttpStatusCode.OK, (await olivia.Advance(row.Batch, "Dose1", etag)).StatusCode);
    }

    [Fact]
    public async Task REQ_BT_028_Real_write_through_the_api_reaches_the_socket_with_its_version()
    {
        var api = Stack.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddHttpClient<ReportFlusher>().ConfigurePrimaryHttpMessageHandler(() => live.Server.CreateHandler())));
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", Env.Origin);
        await client.PostAsJsonAsync("/api/auth/dev-login", new { user = "olivia" });
        using var socket = await ConnectedAsync("olivia");

        var created = await client.PostAsJsonAsync("/api/batches", new { farmId = "farm-a", product = "Vaccine" });

        created.EnsureSuccessStatusCode();
        var frame = await Sockets.ReceiveTypeAsync(socket, "batch");
        Assert.Equal(1, frame.GetProperty("batch").GetProperty("version").GetInt64());
        Assert.Equal(ApiUser.ETagOf(created), frame.GetProperty("etag").GetString());
    }

    [Fact]
    public async Task REQ_BT_028_Summary_frame_follows_a_change_and_repeats_only_when_changed()
    {
        using var socket = await ConnectedAsync("ada");

        var created = Row("farm-a");
        await PostChangeAsync(created with
        {
            Batch = created.Batch with { Pending = new PendingEvent { EventId = "e", TraceId = "t", Status = PendingStatus.Pending } }
        });

        var first = await Sockets.ReceiveTypeAsync(socket, "summary");
        Assert.Equal(1, first.GetProperty("summary").GetProperty("byState").GetProperty("Created").GetInt32());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sockets.ReceiveAsync(socket, TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public async Task REQ_BT_028_Alerts_frame_follows_a_blocked_batch_for_own_farms()
    {
        using var viewer = await ConnectedAsync("viewer");
        var blocked = Row("farm-a");
        var row = blocked with
        {
            Batch = blocked.Batch with
            {
                Pending = new PendingEvent { EventId = "e", TraceId = "t", Status = PendingStatus.Dead, Error = "poison" }
            }
        };

        await PostChangeAsync(row);

        var frame = await Sockets.ReceiveTypeAsync(viewer, "alerts");
        var alert = Assert.Single(frame.GetProperty("alerts").EnumerateArray());
        Assert.Equal(row.Batch.Id, alert.GetProperty("batchId").GetString());
    }

    [Fact]
    public async Task REQ_BT_028_Idle_socket_sends_nothing()
    {
        await WaitForFirstScanAsync();
        using var socket = await ConnectedAsync("ada");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sockets.ReceiveAsync(socket, TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public async Task REQ_BT_028_Two_clients_each_get_the_same_hop()
    {
        using var one = await ConnectedAsync("ada");
        using var two = await ConnectedAsync("ian");

        await PostAsync(NewHop("farm-c", "both"));

        Assert.Equal("both", (await Sockets.ReceiveTypeAsync(one, "hops")).GetProperty("hops")[0].GetProperty("traceId").GetString());
        Assert.Equal("both", (await Sockets.ReceiveTypeAsync(two, "hops")).GetProperty("hops")[0].GetProperty("traceId").GetString());
    }

    [Fact]
    public void REQ_BT_028_Reporter_never_makes_the_caller_wait_for_the_hub()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var http = new HttpClient(new BlockingHandler(entered, release)) { BaseAddress = new Uri("http://localhost/") };
        var reports = new ReportBuffer();

        reports.Report(NewHop("farm-a", "stuck"));
        var flushed = Task.Run(() => new ReportFlusher(http, reports).FlushAsync(default));

        // Report returned before any send began, and a flush stuck in the handler holds no reporter.
        Assert.True(entered.Wait(Limit));
        Assert.False(release.IsSet);
        reports.Report(NewHop("farm-a", "after"));
        Assert.False(flushed.IsCompleted);
        release.Set();
    }
}
