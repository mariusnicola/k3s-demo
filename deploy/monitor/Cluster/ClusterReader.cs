using k8s;
using k8s.Models;

public record Usage(int CpuMilli, int MemoryMi);
public record Snapshot(NodeRow[] Nodes, PodRow[] Pods, CapRow[] Caps, string[] CapIssues, bool AppInstalled, bool ApiAnswered, string? Error, string? DispatcherLeader = null, string? Store = null, WorkerFleet? Workers = null);

/// <summary>Reads what the report shows of the cluster. A failed read gives a snapshot that says so, never an exception.</summary>
public sealed class ClusterReader(
    Kubernetes kube, string controlPlaneName, string appNamespace, string[] namespaces, TimeSpan timeout, TimeSpan usageLimit, string capsPath,
    Func<CancellationToken, Task<Dictionary<string, bool?>>> workerBusy)
{
    public async Task<Snapshot> Collect()
    {
        using var limit = new CancellationTokenSource(timeout);
        var token = limit.Token;
        var host = await ControlPlaneUsage();
        NodeRow ControlPlane(bool ready) => new(controlPlaneName, "control plane", ready, null, host?.CpuMilli, host?.MemoryMi);
        var apiAnswered = false;
        try
        {
            var nodes = await kube.CoreV1.ListNodeAsync(cancellationToken: token);
            apiAnswered = true;
            var file = CapsFile.Load(capsPath);
            var checkedNamespaces = namespaces.Append(appNamespace).ToHashSet();
            var pods = (await kube.CoreV1.ListPodForAllNamespacesAsync(cancellationToken: token)).Items
                .Where(p => PodRules.Shown(p) && checkedNamespaces.Contains(p.Metadata.NamespaceProperty))
                .ToList();
            var appInstalled = (await kube.CoreV1.ListNamespaceAsync(cancellationToken: token)).Items
                .Any(n => n.Metadata.Name == appNamespace);
            var caps = CapCheck.Evaluate(file, Containers(pods.Where(PodRules.HoldsResources)), checkedNamespaces);
            var running = Containers(pods.Where(p => PodRules.HoldsResources(p) || PodRules.Terminating(p)));
            var podRows = PodRows(pods, running, await PodUsage(), await WorkerBusy(token));
            var nodeRows = NodeRows(nodes, await NodeUsage());
            var fleet = file.WorkerMax(appNamespace) is { } max ? new WorkerFleet(max) : null;
            return new Snapshot([ControlPlane(true), .. nodeRows], podRows, caps.Rows, caps.Issues, appInstalled, true, null, await DispatcherLeader(), await StoreInUse(), fleet);
        }
        catch (Exception failure)
        {
            return new Snapshot([ControlPlane(apiAnswered)], [], [], [], false, apiAnswered, failure.Message.ReplaceLineEndings(" "));
        }
    }

    private static List<LiveContainer> Containers(IEnumerable<V1Pod> pods) =>
        pods.SelectMany(p => p.Spec.Containers.Select(c => new LiveContainer(
            p.Metadata.NamespaceProperty, p.Metadata.Name, p.Spec.NodeName ?? "", c.Name,
            Quantity(c.Resources?.Limits, "cpu", 1000m), Quantity(c.Resources?.Limits, "memory", 1m / 1048576)))).ToList();

    private static PodRow[] PodRows(List<V1Pod> pods, List<LiveContainer> live, Dictionary<(string NamespaceProperty, string Name), Usage> usage, Dictionary<string, bool?> busy) =>
        pods.Select(p =>
        {
            var key = (p.Metadata.NamespaceProperty, p.Metadata.Name);
            var containers = live.Where(c => (c.Namespace, c.Pod) == key).ToList();
            usage.TryGetValue(key, out var used);
            return new PodRow(key.NamespaceProperty, key.Name, p.Spec.NodeName ?? "", p.Status.Phase, PodRules.Ready(p),
                containers.Sum(c => c.CpuMilli ?? 0), containers.Sum(c => c.MemoryMi ?? 0), used?.CpuMilli, used?.MemoryMi, PodRules.Terminating(p),
                busy.TryGetValue(key.Name, out var working) ? working : null);
        }).ToArray();

    private static NodeRow[] NodeRows(V1NodeList nodes, Dictionary<string, Usage> usage) =>
        nodes.Items.Select(n =>
        {
            usage.TryGetValue(n.Metadata.Name, out var used);
            return new NodeRow(n.Metadata.Name, "worker", n.Status.Conditions.Any(c => c.Type == "Ready" && c.Status == "True"),
                Quantity(n.Status.Allocatable, "memory", 1m / 1048576) ?? 0, used?.CpuMilli, used?.MemoryMi);
        }).ToArray();

    /// <summary>The control plane is no Kubernetes node, and this container's /proc is the WSL VM that runs it.</summary>
    private static async Task<Usage?> ControlPlaneUsage()
    {
        try
        {
            var before = CpuTicks();
            await Task.Delay(TimeSpan.FromSeconds(1));
            var after = CpuTicks();
            var total = after.Total - before.Total;
            var busy = total - (after.Idle - before.Idle);
            var memory = File.ReadLines("/proc/meminfo").Select(l => l.Split(':', StringSplitOptions.TrimEntries))
                .ToDictionary(p => p[0], p => long.Parse(p[1].Split(' ')[0]));
            return new Usage(total == 0 ? 0 : (int)(busy * 1000 * after.Cores / total), (int)((memory["MemTotal"] - memory["MemAvailable"]) / 1024));
        }
        catch (Exception failure) when (failure is IOException or KeyNotFoundException or FormatException)
        {
            Console.WriteLine($"[WARN] control plane usage: {failure.Message}");
            return null;
        }
    }

    private static (long Total, long Idle, int Cores) CpuTicks()
    {
        var lines = File.ReadAllLines("/proc/stat");
        var ticks = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
        // Fields 3 and 4 are idle and iowait; guest time is already inside user.
        return (ticks.Take(8).Sum(), ticks[3] + ticks[4], lines.Count(l => l.StartsWith("cpu") && char.IsDigit(l[3])));
    }

    /// <summary>A mark on the map, so a failed read shows no leader and takes nothing else down.</summary>
    private Task<string?> DispatcherLeader() => Bounded.Within<string?>(async () =>
        LeaseRule.Leader(await kube.CoordinationV1.ReadNamespacedLeaseAsync("dispatcher", appNamespace), DateTimeOffset.UtcNow), usageLimit, null);

    /// <summary>The ConfigMap the API pods take by envFrom, so their specs hold no value. A mark on the map, as the leader is.</summary>
    private Task<string?> StoreInUse() => Bounded.Within<string?>(async () =>
    {
        var config = await kube.CoreV1.ReadNamespacedConfigMapAsync("batch-tracker-config", appNamespace);
        return config.Data is not null && config.Data.TryGetValue("BATCH_STORE", out var store) ? store : null;
    }, usageLimit, null);

    /// <summary>A pod with no answer stays out of the result, so it reads unknown and never idle.</summary>
    private Task<Dictionary<string, bool?>> WorkerBusy(CancellationToken token) => Bounded.Within(() => workerBusy(token), usageLimit, []);

    /// <summary>Usage is information, so a metrics-server outage must not take the report down with it.</summary>
    private Task<Dictionary<(string NamespaceProperty, string Name), Usage>> PodUsage() => Bounded.Within(async () =>
    {
        var metrics = await kube.GetKubernetesPodsMetricsAsync();
        return metrics.Items.ToDictionary(
            m => (m.Metadata.NamespaceProperty, m.Metadata.Name),
            m => new Usage(
                m.Containers.Sum(c => Quantity(c.Usage, "cpu", 1000m) ?? 0),
                m.Containers.Sum(c => Quantity(c.Usage, "memory", 1m / 1048576) ?? 0)));
    }, usageLimit, []);

    private Task<Dictionary<string, Usage>> NodeUsage() => Bounded.Within(async () =>
    {
        var metrics = await kube.GetKubernetesNodesMetricsAsync();
        return metrics.Items.ToDictionary(
            m => m.Metadata.Name,
            m => new Usage(Quantity(m.Usage, "cpu", 1000m) ?? 0, Quantity(m.Usage, "memory", 1m / 1048576) ?? 0));
    }, usageLimit, []);

    private static int? Quantity(IDictionary<string, ResourceQuantity>? values, string key, decimal scale) =>
        values is not null && values.TryGetValue(key, out var quantity)
            ? (int)Math.Round(quantity.ToDecimal() * scale)
            : null;
}
