using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

// Proves what ping cannot: real UDP and TCP between pods. Ping passed while both failed from Node1.
await new PairProbe().Run();

/// <summary>Tests every peer each round and serves the last result.</summary>
public sealed class PairProbe
{
    private const int HttpPort = 8080;
    private const int UdpPort = 8081;
    private const int UdpTries = 3;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string node = Netprobe.Settings.Required("NODE_NAME");
    private readonly string self = Netprobe.Settings.Required("POD_IP");
    private readonly string peersName = Netprobe.Settings.Required("PEERS");
    private readonly HttpClient http = new() { Timeout = Limit };

    private NetResult latest;
    private long round;
    private string[] known = [];

    public PairProbe() => latest = new NetResult(node, 0, "first round still running", []);

    public async Task Run()
    {
        _ = Task.Run(EchoUdp);
        _ = Task.Run(Serve);

        while (true)
        {
            var started = Stopwatch.StartNew();
            var dnsError = await ReadPeers();
            var peers = await Task.WhenAll(known.Select(TestPeer));
            latest = new NetResult(node, ++round, dnsError, peers);
            var rest = Interval - started.Elapsed;
            if (rest > TimeSpan.Zero) await Task.Delay(rest);
        }
    }

    /// <summary>Peers stay on the last known list, so a DNS failure does not hide what the pair checks see.</summary>
    private async Task<string?> ReadPeers()
    {
        try
        {
            using var dns = new CancellationTokenSource(Limit);
            known = (await Dns.GetHostAddressesAsync(peersName, AddressFamily.InterNetwork, dns.Token))
                .Select(a => a.ToString()).Where(a => a != self).ToArray();
            return null;
        }
        catch (Exception failure)
        {
            return failure.Message;
        }
    }

    private async Task<PeerResult> TestPeer(string ip)
    {
        string? tcpError = null;
        try
        {
            using var answer = await http.GetAsync($"http://{ip}:{HttpPort}/alive");
            answer.EnsureSuccessStatusCode();
        }
        catch (Exception failure)
        {
            tcpError = failure.Message;
        }
        var udp = await TestUdp(ip);
        return new PeerResult(ip, tcpError is null, udp, tcpError);
    }

    private static async Task<bool> TestUdp(string ip)
    {
        // One lost datagram is normal UDP; every try lost is a broken path.
        for (var attempt = 0; attempt < UdpTries; attempt++)
        {
            using var client = new UdpClient();
            client.Connect(ip, UdpPort);
            var sent = RandomNumberGenerator.GetBytes(16);
            try
            {
                using var wait = new CancellationTokenSource(Limit / UdpTries);
                await client.SendAsync(sent, wait.Token);
                var echoed = await client.ReceiveAsync(wait.Token);
                if (echoed.Buffer.SequenceEqual(sent)) return true;
            }
            catch (Exception failure) when (failure is OperationCanceledException or SocketException)
            {
            }
        }
        return false;
    }

    private static async Task EchoUdp()
    {
        using var server = new UdpClient(UdpPort);
        while (true)
        {
            var received = await server.ReceiveAsync();
            await server.SendAsync(received.Buffer, received.RemoteEndPoint);
        }
    }

    private async Task Serve()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{HttpPort}/");
        listener.Start();
        while (true)
        {
            var context = await listener.GetContextAsync();
            await context.Response.OutputStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(latest, Json));
            context.Response.Close();
        }
    }
}

public record PeerResult(string Ip, bool Tcp, bool Udp, string? Error);
public record NetResult(string Node, long Round, string? DnsError, PeerResult[] Peers);
