using System.Text.Json;
using k8s;

// Runs outside the cluster so a cluster failure is reported, not hidden.
await new MonitorApp(args).Run();

/// <summary>Wires the monitor once, in start-up order, and runs its loops until the host stops.</summary>
public sealed class MonitorApp
{
    private const string AppNamespace = "batch-tracker";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
    private static readonly string[] Namespaces = ["kube-system", "metallb-system", "ingress-nginx", "prometheus", "keda"];
    private static readonly string[] Pages = ["/", "/signin", "/alerts", "/dashboard", "/design-system"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string hostIp = Settings.Required("HOST_IP");
    private readonly string ingressOrigin = Settings.Required("INGRESS_ORIGIN");
    private readonly string[] workers = CapsFile.Load("/caps.json").Nodes.Keys.ToArray();
    private readonly string controlPlaneName = Settings.Required("CONTROL_PLANE_NAME");
    private readonly string machineToken = Settings.Required("MONITOR_MACHINE_TOKEN");
    private readonly string mongoConnection = Settings.Required("MONGO_CONNECTION");
    private readonly SessionReader sessions = new(Settings.Required("JWT_PUBLIC_KEY"));

    private readonly SocketHub hub = new(Json);
    private readonly Kubernetes kube;
    private readonly ChangeFeed feed;
    private readonly Operations operations;
    private readonly PodNet podNet;
    private readonly ClusterReader reader;
    private readonly Loops loops;
    private readonly WebApplication app;
    private readonly MetricPoints points;
    private readonly StagePoints stagePoints;
    private readonly ReportChecks reportChecks;
    private readonly ReportCycle cycle;
    private readonly IngressLog ingressLog = new(IngressLog.KeptRows);
    private readonly CancellationToken stopping;

    private Report? latest;
    private int stale;

    public MonitorApp(string[] args)
    {
        var machine = new MachineChecks(ingressOrigin, machineToken, TimeProvider.System);
        kube = new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile("/kubeconfig", masterUrl: $"https://{hostIp}:6443"));
        feed = new ChangeFeed(hub);
        operations = new Operations(hub);
        podNet = new PodNet(kube, AppNamespace, workers, Json);
        var prometheus = new Uri(kube.BaseUri, "api/v1/namespaces/prometheus/services/prometheus-server:80/proxy/");
        var metrics = new MetricSeries((path, token) => kube.HttpClient.GetStringAsync(new Uri(prometheus, path), token));
        reader = new ClusterReader(kube, controlPlaneName, AppNamespace, Namespaces, Timeout, TimeSpan.FromSeconds(5), "/caps.json", metrics.WorkerBusy);
        loops = new Loops(Console.WriteLine, TimeSpan.FromSeconds(5), check => Publish(Loops.Mark(Volatile.Read(ref latest), check, DateTimeOffset.UtcNow)));

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls($"http://+:{Routes.PublicPort}", $"http://+:{Routes.ReportPort}");
        app = builder.Build();
        var socketLimits = SocketLimits.Standard(Environment.GetEnvironmentVariable("TRUSTED_PROXIES"));
        MonitorSocket.Use(app, ingressOrigin, sessions, socketLimits, hub, () => Volatile.Read(ref latest), feed, operations);

        var probeLimits = ProbeLimits.Standard(socketLimits);
        Routes.MapHealth(app, () => Volatile.Read(ref latest), Routes.ReportPort, probeLimits);
        points = new MetricPoints(metrics, hub);
        stagePoints = new StagePoints(metrics, hub);
        reportChecks = new ReportChecks(kube, machine, metrics, hostIp, mongoConnection, ingressOrigin, workers, Namespaces, AppNamespace, Pages, Timeout);
        MetricRoutes.Map(app, sessions, metrics, probeLimits);
        IngressRoutes.Map(app, sessions, ingressLog, probeLimits);
        cycle = new ReportCycle(reader.Collect, () => Volatile.Read(ref latest), Publish);
        stopping = app.Lifetime.ApplicationStopping;
    }

    public async Task Run()
    {
        new ClusterWatches(kube, feed, operations, loops, () => Volatile.Write(ref stale, 1)).Start(stopping);
        _ = new FileTail("/changes/changes.jsonl", feed, operations, loops).Run(stopping);

        _ = loops.Following("ingress log", FollowIngressLog, stopping);
        _ = loops.Forever("metric points", PublishMetricPoints, stopping);
        _ = loops.Forever("stage points", PublishStagePoints, stopping);
        _ = loops.Forever("operations", TickOperations, stopping);
        _ = loops.Forever("refresh", Refresh, stopping);
        _ = loops.Forever("report", CheckAndReport, stopping);

        await app.RunAsync();
    }

    private async Task FollowIngressLog(Action receiving)
    {
        await ingressLog.Follow(async gap =>
        {
            var pods = await kube.CoreV1.ListNamespacedPodAsync("ingress-nginx", labelSelector: "app.kubernetes.io/component=controller", cancellationToken: stopping);
            var pod = pods.Items.First(p => p.Status.Phase == "Running").Metadata.Name;
            return await kube.CoreV1.ReadNamespacedPodLogAsync(pod, "ingress-nginx", container: "controller", follow: true,
                sinceSeconds: gap, tailLines: gap is null ? IngressLog.KeptRows : null, cancellationToken: stopping);
        }, stopping, receiving);
        await Task.Delay(OneSecond, stopping);
    }

    private async Task PublishMetricPoints()
    {
        await Task.Delay(MetricPoints.UntilNext(DateTimeOffset.UtcNow), stopping);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        limit.CancelAfter(Timeout);
        await points.Publish(DateTimeOffset.UtcNow, limit.Token);
    }

    private async Task PublishStagePoints()
    {
        await Task.Delay(StagePoints.UntilNext(DateTimeOffset.UtcNow), stopping);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        limit.CancelAfter(Timeout);
        await stagePoints.Publish(DateTimeOffset.UtcNow, limit.Token);
    }

    private async Task TickOperations()
    {
        operations.Tick(DateTimeOffset.UtcNow);
        await Task.Delay(OneSecond, stopping);
    }

    /// <summary>Nodes, pods and caps follow a change at once. Checks keep their own 30 s cycle and their own time.</summary>
    private async Task Refresh()
    {
        await Task.Delay(OneSecond, stopping);
        if (Interlocked.Exchange(ref stale, 0) != 0) await cycle.Refresh();
    }

    private async Task CheckAndReport()
    {
        var checks = await cycle.Run(async snapshot =>
        {
            var network = snapshot.AppInstalled ? await ReadPodNet() : [];
            var results = await Task.WhenAll(reportChecks.All(snapshot, network));
            // Reaching here is the success, and the wait below would hold an old failure on this report.
            loops.Succeeded("report");
            return [.. results, loops.Check(), HostReport.Check("/changes/host.json", DateTimeOffset.UtcNow), BusThrottle.Check("/changes/wsl.json", DateTimeOffset.UtcNow)];
        });
        var failed = checks.Where(c => !c.Healthy).ToList();
        Console.WriteLine(failed.Count == 0
            ? $"[OK] all {checks.Length} checks healthy"
            : $"[FAIL] {failed.Count} of {checks.Length}: " + string.Join("; ", failed.Select(c => $"{c.Name}: {c.Error}")));
        await Task.Delay(Interval, stopping);
    }

    private void Publish(Report report)
    {
        var previous = Interlocked.Exchange(ref latest, report);
        if (previous is not null) AddDifferences(previous, report);
        hub.Broadcast(report);
    }

    private void AddDifferences(Report old, Report current)
    {
        var checks = old.Checks.ToDictionary(c => c.Name, c => c.Healthy);
        foreach (var check in current.Checks.Where(c => checks.TryGetValue(c.Name, out var was) && was != c.Healthy))
            feed.Add("check", check.Name, Health(!check.Healthy), Health(check.Healthy));
        var sums = old.Caps.ToDictionary(c => (c.Node, c.Resource), c => c.Sum);
        foreach (var cap in current.Caps.Where(c => sums.TryGetValue((c.Node, c.Resource), out var was) && was != c.Sum))
            feed.Add("cap sum", $"{cap.Node} {cap.Resource}", sums[(cap.Node, cap.Resource)].ToString(), cap.Sum.ToString());
    }

    private static string Health(bool healthy) => healthy ? "healthy" : "failing";

    private async Task<IReadOnlyList<NetCheck>> ReadPodNet()
    {
        using var limit = new CancellationTokenSource(Timeout);
        return await podNet.Read(limit.Token);
    }
}
