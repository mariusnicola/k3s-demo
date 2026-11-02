using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>Compares the live cluster to deploy/caps.json. Sums use limits at max replicas.</summary>
public sealed record NodeCap(int CpuMilli, int MemoryMi);
public sealed record CapEntry(string Name, string Node, int CpuMilli, int MemoryMi, string? Namespace, string? Pod, string? Container, int? Replicas, bool Transient);
public sealed record CapsFile(Dictionary<string, NodeCap> Nodes, CapEntry[] Reserved, CapEntry[] Pods)
{
    public static CapsFile Load(string path) =>
        JsonSerializer.Deserialize<CapsFile>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("caps.json is empty");

    /// <summary>Every entry running the Worker container, scaled and fixed alike. Null with none, because none read is not zero Workers.</summary>
    public int? WorkerMax(string appNamespace)
    {
        var entries = Pods.Where(p => p.Namespace == appNamespace && p.Container == "worker").ToList();
        return entries.Count == 0 ? null : entries.Sum(e => e.Replicas ?? 1);
    }
}

public sealed record LiveContainer(string Namespace, string Pod, string Node, string Container, int? CpuMilli, int? MemoryMi);
public sealed record CapRow(string Node, string Resource, int Cap, int Sum, bool Breach);
public sealed record CapResult(CapRow[] Rows, string[] Issues);

public static class CapCheck
{
    public static CapResult Evaluate(CapsFile file, IReadOnlyList<LiveContainer> live, IReadOnlySet<string> namespaces)
    {
        var issues = new List<string>();
        var seen = new Dictionary<CapEntry, HashSet<string>>();
        var liveLimit = new Dictionary<CapEntry, (int Cpu, int Memory)>();

        foreach (var container in live.Where(c => namespaces.Contains(c.Namespace))) Compare(file, container, issues, seen, liveLimit);

        foreach (var (entry, pods) in seen)
        {
            var allowed = (entry.Replicas ?? 1) * (entry.Node == "all" ? file.Nodes.Count : 1);
            if (pods.Count > allowed) issues.Add($"{entry.Name}: {pods.Count} pods, file allows {allowed}");
        }

        return new CapResult(Rows(file, liveLimit), issues.ToArray());
    }

    /// <summary>Holds one live container against its entry in the file.</summary>
    private static void Compare(
        CapsFile file, LiveContainer container, List<string> issues, Dictionary<CapEntry, HashSet<string>> seen, Dictionary<CapEntry, (int Cpu, int Memory)> liveLimit)
    {
        if (file.Reserved.Any(r => r.Namespace == container.Namespace && Regex.IsMatch(container.Pod, r.Pod ?? "$^"))) return;

        var entry = file.Pods.FirstOrDefault(p => p.Namespace == container.Namespace
            && p.Container == container.Container && Regex.IsMatch(container.Pod, p.Pod ?? "$^"));
        if (entry is null)
        {
            issues.Add($"{container.Namespace}/{container.Pod}/{container.Container} not in caps.json");
            return;
        }

        if (!seen.TryGetValue(entry, out var pods)) seen[entry] = pods = [];
        pods.Add(container.Pod);

        if (container.CpuMilli is null || container.MemoryMi is null)
        {
            issues.Add($"{entry.Name}: {container.Pod} has no CPU or memory limit");
            return;
        }
        liveLimit[entry] = (container.CpuMilli.Value, container.MemoryMi.Value);
        if (container.CpuMilli != entry.CpuMilli || container.MemoryMi != entry.MemoryMi)
            issues.Add($"{entry.Name}: limit {container.CpuMilli}m {container.MemoryMi}Mi, file says {entry.CpuMilli}m {entry.MemoryMi}Mi");
        if (entry.Node != "all" && container.Node != entry.Node)
            issues.Add($"{entry.Name}: {container.Pod} on {container.Node}, file says {entry.Node}");
    }

    /// <summary>Each node's sums, from the live limit where one was seen and the file otherwise.</summary>
    private static CapRow[] Rows(CapsFile file, Dictionary<CapEntry, (int Cpu, int Memory)> liveLimit)
    {
        var rows = new List<CapRow>();
        foreach (var (node, cap) in file.Nodes)
        {
            var applies = file.Reserved.Concat(file.Pods).Where(e => e.Node == node || e.Node == "all").ToList();
            var cpu = applies.Sum(e => (liveLimit.TryGetValue(e, out var l) ? l.Cpu : e.CpuMilli) * (e.Replicas ?? 1));
            var memory = applies.Sum(e => (liveLimit.TryGetValue(e, out var l) ? l.Memory : e.MemoryMi) * (e.Replicas ?? 1));
            rows.Add(new CapRow(node, "cpuMilli", cap.CpuMilli, cpu, cpu > cap.CpuMilli));
            rows.Add(new CapRow(node, "memoryMi", cap.MemoryMi, memory, memory > cap.MemoryMi));
        }
        return rows.ToArray();
    }
}
