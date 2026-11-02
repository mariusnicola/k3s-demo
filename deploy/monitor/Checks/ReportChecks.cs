using System.Text.Json;
using k8s;

/// <summary>Every check of one report cycle. Each gets its own time limit, so one stuck check cannot hold the others.</summary>
public sealed class ReportChecks(
    Kubernetes kube, MachineChecks machine, MetricSeries metrics, string hostIp, string mongoConnection, string ingressOrigin,
    string[] workers, string[] namespaces, string appNamespace, string[] pages, TimeSpan timeout, TimeProvider? clock = null)
{
    private readonly ReadyRule readyRule = new(clock ?? TimeProvider.System);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IEnumerable<Task<Check>> All(Snapshot snapshot, IReadOnlyList<NetCheck> network)
    {
        yield return Run("control plane", _ => snapshot.ApiAnswered ? Task.CompletedTask : throw new InvalidOperationException(snapshot.Error));
        foreach (var worker in workers) yield return Run($"node {worker}", token => NodeReady(worker, token));
        foreach (var name in namespaces) yield return Run($"workloads {name}", token => WorkloadsReady(name, token));
        yield return Run("ingress address", IngressAddress);
        yield return Run("service bus", token => RoundTrips.ServiceBus($"{hostIp}:5682", "monitor", token));
        if (snapshot.Store != "mongo") yield return Run("cosmos", token => RoundTrips.Cosmos($"{hostIp}:8091", "monitor", token));
        yield return Run("mongo", token => RoundTrips.Mongo(mongoConnection, "monitor", token));
        yield return Run("caps", _ => CapsHold(snapshot));
        yield return Run("prometheus", metrics.Ready);
        // The app checks run with or without the namespace, so a deleted app reads red, never a smaller green report.
        yield return Run($"workloads {appNamespace}", token => WorkloadsReady(appNamespace, token));
        yield return Run("nightly job", NightlyHealthy);
        foreach (var check in network) yield return Task.FromResult(new Check(check.Name, check.Error is null, check.Error));
        yield return Run("app api ready", ApiReady);
        yield return Run("dispatcher sweep", token => DispatcherSweeping(snapshot, token));
        foreach (var page in pages) yield return Run($"page {page}", token => BrowserPaths.Page(ingressOrigin, page, token));
        yield return Run("ingress /ws", token => BrowserPaths.OperationsOnly(ingressOrigin.Replace("http://", "ws://") + "/ws", ingressOrigin, token));
        yield return Run("ingress /line", machine.Line);
        yield return Run("line bundles", machine.Bundles);
        yield return Run("ingress /ws/hops", machine.Hops);
    }

    private async Task<Check> Run(string name, Func<CancellationToken, Task> check)
    {
        using var limit = new CancellationTokenSource(timeout);
        try
        {
            await check(limit.Token);
            return new Check(name, true, null);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            return new Check(name, false, FormattableString.Invariant($"{name} gave no answer within {timeout.TotalSeconds} s"));
        }
        catch (Exception failure)
        {
            return new Check(name, false, failure.Message.ReplaceLineEndings(" "));
        }
    }

    private async Task NodeReady(string name, CancellationToken token)
    {
        var node = await kube.CoreV1.ReadNodeAsync(name, cancellationToken: token);
        var ready = node.Status.Conditions.FirstOrDefault(c => c.Type == "Ready");
        if (ready?.Status != "True") throw new InvalidOperationException("not Ready");
    }

    private async Task WorkloadsReady(string name, CancellationToken token)
    {
        var deployments = await kube.AppsV1.ListNamespacedDeploymentAsync(name, cancellationToken: token);
        var daemonSets = await kube.AppsV1.ListNamespacedDaemonSetAsync(name, cancellationToken: token);
        if (deployments.Items.Count + daemonSets.Items.Count == 0) throw new InvalidOperationException("nothing deployed");

        var scalers = await kube.AutoscalingV2.ListNamespacedHorizontalPodAutoscalerAsync(name, cancellationToken: token);
        var minimums = scalers.Items
            .Where(s => s.Spec.ScaleTargetRef.Kind == "Deployment")
            .ToDictionary(s => s.Spec.ScaleTargetRef.Name, s => s.Spec.MinReplicas ?? 1);

        var notReady = deployments.Items
            .Where(d => readyRule.Failing($"{name}/{d.Metadata.Name}", d.Spec.Replicas ?? 1, d.Status.ReadyReplicas ?? 0, minimums.GetValueOrDefault(d.Metadata.Name, d.Spec.Replicas ?? 1)))
            .Select(d => d.Metadata.Name)
            .Concat(daemonSets.Items
                .Where(d => d.Status.DesiredNumberScheduled == 0 || d.Status.NumberReady < d.Status.DesiredNumberScheduled)
                .Select(d => d.Metadata.Name))
            .ToList();
        if (notReady.Count > 0) throw new InvalidOperationException("not ready: " + string.Join(",", notReady));
    }

    private async Task NightlyHealthy(CancellationToken token)
    {
        var cron = await kube.BatchV1.ReadNamespacedCronJobAsync("nightly", appNamespace, cancellationToken: token);
        var jobs = await kube.BatchV1.ListNamespacedJobAsync(appNamespace, cancellationToken: token);
        if (NightlyRule.Problem(cron, jobs.Items, DateTimeOffset.UtcNow) is { } problem) throw new InvalidOperationException(problem);
    }

    private async Task IngressAddress(CancellationToken token)
    {
        var service = await kube.CoreV1.ReadNamespacedServiceAsync("ingress-nginx-controller", "ingress-nginx", cancellationToken: token);
        if (service.Status.LoadBalancer?.Ingress is not { Count: > 0 }) throw new InvalidOperationException("no external address");
    }

    /// <summary>
    /// The API proxy reaches the pod over the agent tunnel, and the pod checks its store and Service Bus
    /// from inside, so this sees the Hyper-V firewall path that a check from WSL cannot see.
    /// </summary>
    private Task ApiReady(CancellationToken token) =>
        kube.CoreV1.ConnectGetNamespacedServiceProxyWithPathAsync("http:api:8080", appNamespace, "api/ready/deep", cancellationToken: token);

    private async Task DispatcherSweeping(Snapshot snapshot, CancellationToken token)
    {
        if (snapshot.DispatcherLeader is not { } holder) throw new InvalidOperationException("lease names no live Dispatcher, or could not be read");
        using var stream = await kube.CoreV1.ConnectGetNamespacedPodProxyWithPathAsync($"{holder}:8080", appNamespace, "status", cancellationToken: token);
        var state = await JsonSerializer.DeserializeAsync<DispatcherState>(stream, Json, token) ?? throw new InvalidOperationException($"{holder} sent an empty status");
        if (SweepRule.Problem(holder, state) is { } problem) throw new InvalidOperationException(problem);
    }

    private Task CapsHold(Snapshot snapshot)
    {
        if (snapshot.Error is not null) throw new InvalidOperationException(snapshot.Error);
        var breaches = snapshot.Caps.Where(c => c.Breach).Select(c => $"{c.Node} {c.Resource} {c.Sum} over {c.Cap}");
        var problems = breaches.Concat(snapshot.CapIssues).ToList();
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("; ", problems));
        return Task.CompletedTask;
    }
}
