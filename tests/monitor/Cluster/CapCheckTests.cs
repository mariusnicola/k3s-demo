using Xunit;

public class CapCheckTests
{
    private static readonly HashSet<string> Checked = ["app"];

    private static CapEntry Entry(string name = "api", string node = "w1", int cpu = 500, int memory = 256, int? replicas = null) =>
        new(name, node, cpu, memory, "app", $"^{name}", "c", replicas, false);

    private static CapEntry Reserved(int cpu, int memory) => new("system", "w1", cpu, memory, "app", "^svclb", null, null, false);

    private static CapsFile File(CapEntry[] pods, CapEntry[]? reserved = null, int cap = 1000, params string[] nodes) =>
        new((nodes.Length == 0 ? ["w1"] : nodes).ToDictionary(n => n, _ => new NodeCap(cap, cap)), reserved ?? [], pods);

    private static LiveContainer Live(string pod = "api-1", string node = "w1", int? cpu = 500, int? memory = 256, string space = "app", string container = "c") =>
        new(space, pod, node, container, cpu, memory);

    private static CapResult Evaluate(CapsFile file, params LiveContainer[] live) => CapCheck.Evaluate(file, live, Checked);

    [Fact]
    public void A_container_that_matches_its_entry_gives_no_issue_and_sums_limit_times_replicas()
    {
        var result = Evaluate(File([Entry(replicas: 2)]), Live());

        Assert.Empty(result.Issues);
        Assert.Equal(
            [new CapRow("w1", "cpuMilli", 1000, 1000, false), new CapRow("w1", "memoryMi", 1000, 512, false)],
            result.Rows);
    }

    [Fact]
    public void A_sum_over_the_cap_is_a_breach_and_one_at_the_cap_is_not()
    {
        var result = Evaluate(File([Entry(cpu: 1001, memory: 1000)]), Live(cpu: 1001, memory: 1000));

        Assert.True(result.Rows.Single(r => r.Resource == "cpuMilli").Breach);
        Assert.False(result.Rows.Single(r => r.Resource == "memoryMi").Breach);
    }

    [Fact]
    public void A_reserved_pod_needs_no_entry_and_its_file_values_count_in_the_sum()
    {
        var result = Evaluate(File([Entry()], [Reserved(100, 10)]), Live(), Live("svclb-1", cpu: 999, memory: 999));

        Assert.Empty(result.Issues);
        Assert.Equal(600, result.Rows[0].Sum);
        Assert.Equal(266, result.Rows[1].Sum);
    }

    [Fact]
    public void A_container_with_no_entry_is_named()
    {
        var result = Evaluate(File([Entry()]), Live("stray-1"));

        Assert.Equal(["app/stray-1/c not in caps.json"], result.Issues);
    }

    [Fact]
    public void A_container_of_another_entry_name_or_container_is_named()
    {
        var result = Evaluate(File([Entry()]), Live(container: "sidecar"));

        Assert.Equal(["app/api-1/sidecar not in caps.json"], result.Issues);
    }

    [Fact]
    public void A_namespace_outside_the_checked_set_is_ignored()
    {
        var result = Evaluate(File([Entry()]), Live("stray-1", space: "elsewhere"));

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void A_container_with_no_limit_is_the_only_issue_and_the_file_value_stays_in_the_sum()
    {
        var result = Evaluate(File([Entry()]), Live(node: "w2", cpu: null));

        Assert.Equal(["api: api-1 has no CPU or memory limit"], result.Issues);
        Assert.Equal(500, result.Rows[0].Sum);
    }

    [Fact]
    public void A_limit_that_differs_from_the_file_is_named_and_the_live_limit_is_summed()
    {
        var result = Evaluate(File([Entry(replicas: 2)]), Live(cpu: 600, memory: 300));

        Assert.Equal(["api: limit 600m 300Mi, file says 500m 256Mi"], result.Issues);
        Assert.Equal(1200, result.Rows[0].Sum);
        Assert.Equal(600, result.Rows[1].Sum);
    }

    [Fact]
    public void A_pod_on_the_wrong_node_is_named()
    {
        var result = Evaluate(File([Entry()]), Live(node: "w2"));

        Assert.Equal(["api: api-1 on w2, file says w1"], result.Issues);
    }

    [Fact]
    public void An_entry_on_all_nodes_runs_anywhere_and_counts_on_each_node()
    {
        var result = Evaluate(File([Entry(node: "all")], nodes: ["w1", "w2"]), Live(node: "w2"));

        Assert.Empty(result.Issues);
        Assert.Equal(["w1", "w1", "w2", "w2"], result.Rows.Select(r => r.Node));
        Assert.Equal([500, 256, 500, 256], result.Rows.Select(r => r.Sum));
    }

    [Fact]
    public void More_pods_than_replicas_is_named()
    {
        var result = Evaluate(File([Entry(replicas: 2)]), Live("api-1"), Live("api-2"), Live("api-3"));

        Assert.Equal(["api: 3 pods, file allows 2"], result.Issues);
    }

    [Fact]
    public void An_entry_with_no_replicas_allows_one_pod()
    {
        var result = Evaluate(File([Entry()]), Live("api-1"), Live("api-2"));

        Assert.Equal(["api: 2 pods, file allows 1"], result.Issues);
    }

    [Fact]
    public void An_entry_on_all_nodes_allows_its_replicas_on_each_node()
    {
        var allowed = Evaluate(File([Entry(node: "all")], nodes: ["w1", "w2"]), Live("api-1"), Live("api-2"));
        var over = Evaluate(File([Entry(node: "all")], nodes: ["w1", "w2"]), Live("api-1"), Live("api-2"), Live("api-3"));

        Assert.Empty(allowed.Issues);
        Assert.Equal(["api: 3 pods, file allows 2"], over.Issues);
    }

    [Fact]
    public void Two_rows_for_one_pod_count_as_one_pod()
    {
        var result = Evaluate(File([Entry()]), Live("api-1"), Live("api-1"));

        Assert.Empty(result.Issues);
    }
}
