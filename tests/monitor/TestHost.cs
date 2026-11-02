using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

/// <summary>
/// Every test host. No test chooses a port and lets it go before the host binds, because classes run at the same
/// time and any of them can take a port in that gap.
/// </summary>
public static class TestHost
{
    /// <summary>A host the kernel gives a port as it binds. Read the address with <see cref="Url"/> after start.</summary>
    public static WebApplication Create()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        return builder.Build();
    }

    public static string Url(WebApplication host) => host.Urls.Single();

    /// <summary>
    /// For a route that needs a port number before start. The sockets are bound here and stay held until the host
    /// has them, so the number is known and the port is never free.
    /// </summary>
    public static (WebApplication Host, int[] Ports, Action Release) CreateHolding(int ports)
    {
        var held = Enumerable.Range(0, ports).Select(_ => Hold()).ToArray();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            foreach (var socket in held) kestrel.ListenHandle((ulong)socket.Handle);
        });
        return (builder.Build(), held.Select(s => ((IPEndPoint)s.LocalEndPoint!).Port).ToArray(), () =>
        {
            foreach (var socket in held) socket.Dispose();
        });
    }

    private static Socket Hold()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen();
        return socket;
    }
}
