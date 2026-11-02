using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

// Runs the same checks from WSL and from inside a pod.
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
return await Commands.Run(args, timeout.Token);

/// <summary>One command per smoke check. The exit code is the result.</summary>
public static class Commands
{
    public static Task<int> Run(string[] args, CancellationToken token) => args[0] switch
    {
        "serve" => Serve(int.Parse(args[1])),
        "dns" => Lookup(args[1], token),
        "tcp" => Connect(args[1], int.Parse(args[2]), args.Length > 3 ? int.Parse(args[3]) : 0, token),
        "http" => Get(args, token),
        "sb" => Done(RoundTrips.ServiceBus(args[1], "queue.1", token)),
        "cosmos" => Done(RoundTrips.Cosmos(args[1], "smoke", token)),
        "ws" => Done(BrowserPaths.OperationsOnly(args[1], args.Length > 2 ? args[2] : "", token)),
        // The session comes by environment, so a failed run cannot print it with its arguments.
        "ws-report" => Done(BrowserPaths.Report(args[1], args[2], Session(), token)),
        "ws-hold" => Hold(args),
        "ws-wait" => Wait(args),
        "page" => Done(BrowserPaths.Page(args[1], args[2], token)),
        "unauthorized" => Done(BrowserPaths.Unauthorized(args[1], args[2], token)),
        "overdue-seed" => Done(OverdueBatch.Seed(args[1], token)),
        "overdue-flags" => Done(OverdueBatch.AssertFlags(args[1], int.Parse(args[2]), token)),
        "overdue-delete" => Done(OverdueBatch.Delete(args[1], args.Length > 2 ? args[2] : OverdueBatch.Farm, token)),
        _ => Unknown(args[0])
    };

    private static async Task<int> Done(Task command)
    {
        await command;
        return 0;
    }

    private static Task<int> Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command {command}");
        return Task.FromResult(2);
    }

    private static async Task<int> Lookup(string host, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, token);
        return addresses.Length > 0 ? 0 : 1;
    }

    private static async Task<int> Connect(string host, int port, int sourcePort, CancellationToken token)
    {
        using var tcp = sourcePort > 0 ? FromPort(sourcePort) : new TcpClient();
        await tcp.ConnectAsync(host, port, token);
        return 0;
    }

    /// <summary>A row names the source port to meet a port NAT already holds, which a kernel-chosen port only does by chance.</summary>
    private static TcpClient FromPort(int sourcePort)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork);
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        tcp.Client.Bind(new IPEndPoint(IPAddress.Any, sourcePort));
        return tcp;
    }

    private static async Task<int> Get(string[] args, CancellationToken token)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, args[1]);
        if (args.Length > 2) request.Headers.Host = args[2];
        (await http.SendAsync(request, token)).EnsureSuccessStatusCode();
        return 0;
    }

    /// <summary>Outlasts nginx's default 60 s read timeout.</summary>
    private static async Task<int> Hold(string[] args)
    {
        using var hold = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await BrowserPaths.Hold(args[1], args[2], TimeSpan.FromSeconds(65), hold.Token);
        return 0;
    }

    private static async Task<int> Wait(string[] args)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(args[4])));
        DateTimeOffset? since = args.Length > 5 && args[5].Length > 0 ? DateTimeOffset.Parse(args[5]) : null;
        Console.WriteLine(await BrowserPaths.Wait(args[1], args[2], Session(), new Regex(args[3]), since, wait.Token));
        return 0;
    }

    private static string Session() => Environment.GetEnvironmentVariable("PROBE_SESSION") ?? throw new InvalidOperationException("PROBE_SESSION not set");

    private static Task<int> Serve(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{port}/");
        listener.Start();
        while (true)
        {
            var context = listener.GetContext();
            context.Response.StatusCode = 200;
            context.Response.Close();
        }
    }
}
