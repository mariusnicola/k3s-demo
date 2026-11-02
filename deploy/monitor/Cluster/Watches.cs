using k8s;
using k8s.Models;

/// <summary>Watches, so a change shows as it happens and not on the next 30 s cycle.</summary>
public sealed class ClusterWatches(Kubernetes kube, ChangeFeed feed, Operations operations, Loops loops, Action changed)
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(2);
    private static readonly string[] Rollouts = ["batch-tracker", "ingress-nginx", "keda", "metallb-system", "prometheus"];

    public void Start(CancellationToken token)
    {
        _ = Follow("nodes",
            async t => { var l = await kube.CoreV1.ListNodeAsync(cancellationToken: t); return (l.Items, l.Metadata.ResourceVersion); },
            (rv, t) => kube.CoreV1.WatchListNodeAsync(resourceVersion: rv, cancellationToken: t),
            n => n.Metadata.Name, Node, token);
        _ = Follow("pods",
            async t => { var l = await kube.CoreV1.ListPodForAllNamespacesAsync(cancellationToken: t); return (l.Items, l.Metadata.ResourceVersion); },
            (rv, t) => kube.CoreV1.WatchListPodForAllNamespacesAsync(resourceVersion: rv, cancellationToken: t),
            p => Name(p.Metadata), Pod, token);
        _ = Follow("deployments",
            async t => { var l = await kube.AppsV1.ListDeploymentForAllNamespacesAsync(cancellationToken: t); return (l.Items, l.Metadata.ResourceVersion); },
            (rv, t) => kube.AppsV1.WatchListDeploymentForAllNamespacesAsync(resourceVersion: rv, cancellationToken: t),
            d => Name(d.Metadata), Deployment, token);
        _ = Follow("autoscalers",
            async t => { var l = await kube.AutoscalingV2.ListHorizontalPodAutoscalerForAllNamespacesAsync(cancellationToken: t); return (l.Items, l.Metadata.ResourceVersion); },
            (rv, t) => kube.AutoscalingV2.WatchListHorizontalPodAutoscalerForAllNamespacesAsync(resourceVersion: rv, cancellationToken: t),
            a => Name(a.Metadata), Autoscaler, token);
        _ = Follow("helm releases",
            async t => { var l = await kube.CoreV1.ListSecretForAllNamespacesAsync(labelSelector: "owner=helm", cancellationToken: t); return (l.Items, l.Metadata.ResourceVersion); },
            (rv, t) => kube.CoreV1.WatchListSecretForAllNamespacesAsync(labelSelector: "owner=helm", resourceVersion: rv, cancellationToken: t),
            s => Name(s.Metadata), Release, token);
    }

    private static string Name(V1ObjectMeta meta) => $"{meta.NamespaceProperty}/{meta.Name}";

    /// <summary>Lists first, so a reconnect compares against what was known and a missed event still shows.</summary>
    private async Task Follow<T>(string what, Func<CancellationToken, Task<(IList<T> Items, string Version)>> list,
        Func<string, CancellationToken, IAsyncEnumerable<(WatchEventType, T)>> watch, Func<T, string> key, Action<T?, T?> compare, CancellationToken token)
        where T : class
    {
        var known = new KnownItems<T>(key, compare, changed);
        var name = $"watch {what}";
        await loops.Following(name, async receiving =>
        {
            try
            {
                var (items, version) = await list(token);
                known.Seed(items);
                // The list is the proof: a quiet watch can go hours with no event, and a read of the API just answered.
                receiving();
                await foreach (var (type, item) in watch(version, token)) known.Event(type, item);
            }
            catch (Exception failure) when (!token.IsCancellationRequested)
            {
                var reason = failure.Message.ReplaceLineEndings(" ");
                Console.WriteLine($"[WARN] {name}: {reason}");
                if (!TimedOutQuietly(failure)) loops.Failed(name, reason);
            }
            await Task.Delay(Retry, token);
        }, token);
    }

    /// <summary>
    /// The client reads a watch's first line while opening it, so a normal timeout with no event on that connection
    /// surfaces as this exception. A real failure then shows on the next list, 2 s later.
    /// </summary>
    private static bool TimedOutQuietly(Exception failure) => failure is HttpRequestException { InnerException: EndOfStreamException };

    /// <summary>What one watch has seen. Every change goes through compare first, then tells the report to refresh.</summary>
    private sealed class KnownItems<T>(Func<T, string> key, Action<T?, T?> compare, Action changed)
        where T : class
    {
        private Dictionary<string, T> known = [];
        private bool seeded;

        public void Seed(IList<T> items)
        {
            var current = items.ToDictionary(key);
            if (seeded)
            {
                foreach (var (name, item) in current) Apply(known.GetValueOrDefault(name), item);
                foreach (var (name, item) in known.Where(k => !current.ContainsKey(k.Key))) Apply(item, null);
            }
            known = current;
            seeded = true;
        }

        public void Event(WatchEventType type, T item)
        {
            var name = key(item);
            if (type == WatchEventType.Deleted)
            {
                Apply(known.GetValueOrDefault(name), null);
                known.Remove(name);
            }
            else if (type is WatchEventType.Added or WatchEventType.Modified)
            {
                Apply(known.GetValueOrDefault(name), item);
                known[name] = item;
            }
        }

        private void Apply(T? old, T? current)
        {
            compare(old, current);
            changed();
        }
    }

    private void Node(V1Node? old, V1Node? current)
    {
        if (current is null)
        {
            feed.Add("node removed", old!.Metadata.Name, null, null);
            return;
        }
        static string Ready(V1Node n) => n.Status?.Conditions?.FirstOrDefault(c => c.Type == "Ready")?.Status ?? "Unknown";
        if (old is null) feed.Add("node added", current.Metadata.Name, null, Ready(current));
        else if (Ready(old) != Ready(current)) feed.Add("node ready", current.Metadata.Name, Ready(old), Ready(current));
    }

    private void Pod(V1Pod? old, V1Pod? current)
    {
        if (current is null)
        {
            if (old!.Metadata.DeletionTimestamp is null) feed.Add("pod deleted", Name(old.Metadata), old.Spec?.NodeName, null);
            return;
        }
        var name = Name(current.Metadata);
        // A pod can take its whole grace period to go, and the delete happened when this appeared.
        if (old?.Metadata.DeletionTimestamp is null && current.Metadata.DeletionTimestamp is not null) feed.Add("pod deleted", name, current.Spec?.NodeName, null);
        if (old is null) feed.Add("pod created", name, null, current.Status?.Phase);
        if (string.IsNullOrEmpty(old?.Spec?.NodeName) && !string.IsNullOrEmpty(current.Spec?.NodeName)) feed.Add("pod scheduled", name, null, current.Spec.NodeName);
        var before = Restarts(old);
        var after = Restarts(current);
        if (old is not null && after > before) feed.Add("pod restarted", name, before.ToString(), after.ToString());
        var failure = Failure(current);
        if (failure is not null && failure != Failure(old)) feed.Add("pod failed", name, Failure(old), failure);
    }

    private static int Restarts(V1Pod? pod) => pod?.Status?.ContainerStatuses?.Sum(c => c.RestartCount) ?? 0;

    private static string? Failure(V1Pod? pod) =>
        pod?.Status?.Phase == "Failed" ? pod.Status.Reason ?? "Failed"
        : pod?.Status?.ContainerStatuses?.Select(c => c.State?.Waiting?.Reason).FirstOrDefault(r => r is "CrashLoopBackOff" or "ImagePullBackOff" or "ErrImagePull" or "ErrImageNeverPull" or "CreateContainerConfigError");

    private void Deployment(V1Deployment? old, V1Deployment? current)
    {
        if (current is null)
        {
            feed.Add("deployment deleted", Name(old!.Metadata), Images(old), null);
            return;
        }
        var name = Name(current.Metadata);
        if (old is null) feed.Add("deployment created", name, null, Images(current));
        else
        {
            if (old.Spec?.Replicas != current.Spec?.Replicas) feed.Add("deployment replicas", name, old.Spec?.Replicas?.ToString(), current.Spec?.Replicas?.ToString());
            if (Images(old) != Images(current)) feed.Add("deployment image", name, Images(old), Images(current));
            if (old.Status?.ReadyReplicas != current.Status?.ReadyReplicas) feed.Add("deployment ready", name, (old.Status?.ReadyReplicas ?? 0).ToString(), (current.Status?.ReadyReplicas ?? 0).ToString());
        }
        if (Rollouts.Contains(current.Metadata.NamespaceProperty))
            operations.Rollout($"rollout:{name}", $"Rollout {name}", Progressing(current), DateTimeOffset.UtcNow);
    }

    private static string Images(V1Deployment deployment) => string.Join(",", deployment.Spec?.Template?.Spec?.Containers?.Select(c => c.Image) ?? []);

    /// <summary>Old pods exist only during a rollout: a scale changes the count, and every pod stays updated.</summary>
    private static bool Progressing(V1Deployment deployment) =>
        (deployment.Status?.UpdatedReplicas ?? 0) < (deployment.Status?.Replicas ?? 0);

    private void Autoscaler(V2HorizontalPodAutoscaler? old, V2HorizontalPodAutoscaler? current)
    {
        if (old is null || current is null || !current.Metadata.Name.StartsWith("keda-hpa-")) return;
        if (old.Status?.DesiredReplicas != current.Status?.DesiredReplicas)
            feed.Add("keda scale", Name(current.Metadata), old.Status?.DesiredReplicas.ToString(), current.Status?.DesiredReplicas.ToString());
    }

    /// <summary>Reads labels only: the release body in these Secrets stays unread.</summary>
    private void Release(V1Secret? old, V1Secret? current)
    {
        if (current is null) return;
        static string Label(V1Secret secret, string name) => secret.Metadata.Labels is { } labels && labels.TryGetValue(name, out var value) ? value : "";
        var state = $"{Label(current, "version")} {Label(current, "status")}";
        if (old is null || Label(old, "status") != Label(current, "status"))
            feed.Add("helm release", $"{current.Metadata.NamespaceProperty}/{Label(current, "name")}", old is null ? null : $"{Label(old, "version")} {Label(old, "status")}", state);
    }
}
