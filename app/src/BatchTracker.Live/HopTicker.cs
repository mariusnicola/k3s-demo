using System.Security.Claims;
using BatchTracker.Infrastructure;

namespace BatchTracker.Live;

public sealed class HopTicker(HopHub hub) : BackgroundService
{
    private const int TicksPerReport = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var ticks = 0;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            hub.Tick();
            if (++ticks % TicksPerReport == 0) hub.PublishReports();
        }
    }
}

public static class HopSocket
{
    public static Task RunAsync(HttpContext context, ClaimsPrincipal user, HopHub hub) =>
        SocketSession.RunAsync(context, user, hub.Connect, hub.Disconnect);
}
