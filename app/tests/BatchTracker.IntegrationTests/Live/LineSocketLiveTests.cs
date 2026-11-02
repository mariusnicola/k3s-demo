using BatchTracker.Domain;
using BatchTracker.Line;
using Microsoft.Extensions.Logging.Abstractions;

namespace BatchTracker.IntegrationTests;

/// <summary>The real Live, the real sign-in and the real hub, so the frame's shape and the machine's farms are not assumed.</summary>
public class LineSocketLiveTests(Stack stack) : LiveTest(stack)
{
    private LineSocket NewSocket(LineSettled settled)
    {
        // Started here, because the socket's task and the test's would otherwise race to start the host and end on two.
        var server = Server;
        var options = new LineOptions
        {
            MachineToken = Env.LineMachineToken,
            SocketRetry = TimeSpan.FromMilliseconds(100),
            OpenSocket = (bearer, _) => Sockets.OpenAsync(server, "/ws/hops", null, null, bearer)
        };
        var http = Stack.Api.CreateClient();
        http.BaseAddress = new Uri("http://localhost/api/");
        return new LineSocket(new LineApi(http, options, TimeProvider.System, settled), settled, options, NullLogger<LineSocket>.Instance);
    }

    [Theory]
    [InlineData("farm-a")]
    [InlineData("farm-b")]
    public async Task REQ_BT_045_A_settled_row_reaches_a_wait_over_the_machine_token(string farm)
    {
        var settled = new LineSettled();
        using var socket = NewSocket(settled);
        var row = Row(farm, version: 3, BatchState.Dose1);
        using var watch = settled.Watch(row.Batch.Id);
        using var limit = new CancellationTokenSource(Limit);
        await socket.StartAsync(limit.Token);

        // The socket is up a moment before the hub lists it, and a frame sent in that moment is lost, as a real one may be.
        SettledBatch? seen = null;
        while (seen is null)
        {
            await PostChangeAsync(row);
            seen = await watch.AfterAsync(2, TimeSpan.FromMilliseconds(50), limit.Token);
        }
        await socket.StopAsync(CancellationToken.None);

        Assert.Equal(row.ETag, seen.ETag);
        Assert.Equal(BatchState.Dose1, seen.State);
    }
}
