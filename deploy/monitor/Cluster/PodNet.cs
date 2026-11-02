using System.Text.Json;
using k8s;

public sealed record PeerResult(string Ip, bool Tcp, bool Udp, string? Error);
public sealed record NetResult(string Node, long Round, string? DnsError, PeerResult[] Peers);
public sealed record NetCheck(string Name, string? Error);

/// <summary>Results come through the API server, so a broken pod network cannot also hide the report of it.</summary>
public sealed class PodNet(Kubernetes kube, string appNamespace, string[] nodes, JsonSerializerOptions json)
{
    private const string Selector = "app.kubernetes.io/component=netprobe";
    private readonly Dictionary<string, long> lastRound = [];

    public async Task<IReadOnlyList<NetCheck>> Read(CancellationToken token)
    {
        Dictionary<string, (string Pod, string Ip)> pods;
        try
        {
            var list = await kube.CoreV1.ListNamespacedPodAsync(appNamespace, labelSelector: Selector, cancellationToken: token);
            pods = list.Items.Where(p => p.Status.Phase == "Running" && p.Status.PodIP is not null)
                .GroupBy(p => p.Spec.NodeName)
                .ToDictionary(g => g.Key, g => (g.First().Metadata.Name, g.First().Status.PodIP));
        }
        catch (Exception failure)
        {
            return Everything($"probe pods unreadable: {failure.Message}");
        }

        var results = await Task.WhenAll(nodes.Select(async node => (node, result: await Fetch(pods, node, token))));
        return results.SelectMany(r => Evaluate(r.node, r.result, pods)).ToList();
    }

    private async Task<(NetResult? Result, string? Error)> Fetch(Dictionary<string, (string Pod, string Ip)> pods, string node, CancellationToken token)
    {
        if (!pods.TryGetValue(node, out var pod)) return (null, "no probe pod running");
        try
        {
            using var stream = await kube.CoreV1.ConnectGetNamespacedPodProxyWithPathAsync($"{pod.Pod}:8080", appNamespace, "result", cancellationToken: token);
            var result = await JsonSerializer.DeserializeAsync<NetResult>(stream, json, token)
                ?? throw new InvalidOperationException("empty result");
            // A round number needs no shared clock, and these nodes do not share one.
            var stale = lastRound.TryGetValue(node, out var last) && last == result.Round;
            lastRound[node] = result.Round;
            return stale ? (null, $"result stale at round {result.Round}") : (result, null);
        }
        catch (Exception failure)
        {
            return (null, $"result unreadable: {failure.Message.ReplaceLineEndings(" ")}");
        }
    }

    private IEnumerable<NetCheck> Evaluate(string from, (NetResult? Result, string? Error) read, Dictionary<string, (string Pod, string Ip)> pods)
    {
        var result = read.Result;
        yield return new NetCheck($"pod dns {from}", read.Error ?? result!.DnsError);
        foreach (var to in nodes.Where(n => n != from))
        {
            yield return new NetCheck($"pod net {from} to {to}", read.Error ?? Pair(result!, to, pods));
        }
    }

    private static string? Pair(NetResult result, string to, Dictionary<string, (string Pod, string Ip)> pods)
    {
        if (!pods.TryGetValue(to, out var peer)) return "no peer pod running";
        var tested = result.Peers.FirstOrDefault(p => p.Ip == peer.Ip);
        if (tested is null) return "peer never tested";
        if (tested.Tcp && tested.Udp) return null;
        return string.Join(", ", new[] { tested.Tcp ? null : $"tcp failed: {tested.Error}", tested.Udp ? null : "udp failed" }.OfType<string>());
    }

    private IReadOnlyList<NetCheck> Everything(string error) =>
        nodes.SelectMany(from => nodes.Where(to => to != from).Select(to => new NetCheck($"pod net {from} to {to}", error))
            .Prepend(new NetCheck($"pod dns {from}", error))).ToList();
}
