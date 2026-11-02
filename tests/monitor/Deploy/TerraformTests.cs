using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>Setup hands the charts, the MetalLB pool and the app namespace to Terraform, and Terraform declares each of them.</summary>
public class TerraformTests
{
    private static string Root([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    private static string Setup => File.ReadAllText(Path.Combine(Root(), "deploy", "setup.ps1"));

    private static string TerraformDir => Path.Combine(Root(), "deploy", "terraform");

    private static string Tf() =>
        string.Join("\n", Directory.GetFiles(TerraformDir, "*.tf").OrderBy(f => f).Select(File.ReadAllText));

    /// <summary>Body of one PowerShell function, up to the next top-level function.</summary>
    private static string Function(string name)
    {
        var from = Setup.IndexOf("function " + name, StringComparison.Ordinal);
        Assert.True(from >= 0, $"setup.ps1 has no function {name}");
        var next = Setup.IndexOf("\nfunction ", from + 1, StringComparison.Ordinal);
        return next < 0 ? Setup[from..] : Setup[from..next];
    }

    /// <summary>Body of one Terraform block, braces balanced.</summary>
    private static string Block(string kind, string type, string label)
    {
        var header = Regex.Match(Tf(), $@"{kind} ""{type}"" ""{Regex.Escape(label)}"" \{{");
        Assert.True(header.Success, $"no {kind} {type} {label} in deploy/terraform");
        var depth = 0;
        var tf = Tf();
        for (var i = header.Index + header.Length - 1; i < tf.Length; i++)
        {
            if (tf[i] == '{') depth++;
            if (tf[i] == '}' && --depth == 0) return tf[header.Index..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced block " + label);
    }

    private static (string Name, string Namespace)[] SetupCharts()
    {
        var list = Regex.Match(Setup, @"\$Charts = @\((.*?)\n\)", RegexOptions.Singleline);
        Assert.True(list.Success, "setup.ps1 declares no $Charts");
        return Regex.Matches(list.Groups[1].Value, @"Name = '([^']+)';\s*Namespace = '([^']+)'")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToArray();
    }

    [Fact]
    public void Setup_lists_the_four_charts_in_install_order()
    {
        Assert.Equal(new[] { "metallb", "ingress-nginx", "prometheus", "keda" }, SetupCharts().Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Each_chart_setup_rolls_out_has_a_release_with_its_name_and_namespace()
    {
        foreach (var (name, ns) in SetupCharts())
        {
            var release = Block("resource", "helm_release", name);
            Assert.Contains($"name             = \"{name}\"", release);
            Assert.Contains($"namespace        = \"{ns}\"", release);
            Assert.Contains("create_namespace = true", release);
        }
    }

    [Fact]
    public void Each_chart_release_pins_an_exact_version_and_a_repository_and_setup_holds_neither()
    {
        foreach (var (name, _) in SetupCharts())
        {
            var release = Block("resource", "helm_release", name);
            Assert.Matches(@"version\s+= ""\d+\.\d+\.\d+""", release);
            Assert.Matches(@"repository\s+= ""https://", release);
            Assert.Matches($@"chart\s+= ""{Regex.Escape(name)}""", release);
        }
        Assert.DoesNotMatch(@"\$Charts = @\([^)]*(Version|Repo) =", Setup);
    }

    [Fact]
    public void Each_chart_release_reads_cluster_values_then_caps_values_in_that_order()
    {
        foreach (var (name, _) in SetupCharts())
        {
            var release = Block("resource", "helm_release", name);
            var cluster = release.IndexOf($"file(\"${{path.module}}/../cluster/{name}.yaml\")", StringComparison.Ordinal);
            var caps = release.IndexOf($"file(\"${{var.generated_dir}}/{name}.json\")", StringComparison.Ordinal);
            Assert.True(cluster >= 0 && caps > cluster, $"{name}: cluster values must come before caps values");
        }
    }

    [Fact]
    public void Each_chart_release_returns_without_waiting_and_keeps_ten_revisions()
    {
        foreach (var (name, _) in SetupCharts())
        {
            var release = Block("resource", "helm_release", name);
            Assert.Matches(@"wait\s+= false", release);
            Assert.Matches(@"max_history\s+= 10", release);
        }
    }

    [Fact]
    public void The_app_release_waits_300_seconds_on_the_release_chart_and_the_generated_values()
    {
        var release = Block("resource", "helm_release", "batch-tracker");
        Assert.Contains("name        = \"batch-tracker\"", release);
        Assert.Contains("namespace   = \"batch-tracker\"", release);
        Assert.Contains("chart       = \"${path.module}/../app\"", release);
        Assert.Contains("file(\"${var.generated_dir}/batch-tracker.json\")", release);
        Assert.Matches(@"wait\s+= true", release);
        Assert.Matches(@"timeout\s+= 300", release);
        Assert.Matches(@"max_history\s+= 10", release);
        Assert.DoesNotContain("create_namespace", release);
        Assert.Contains("kubernetes_namespace_v1.batch-tracker", release);
    }

    [Fact]
    public void The_app_namespace_is_a_resource()
    {
        Assert.Contains("name = \"batch-tracker\"", Block("resource", "kubernetes_namespace_v1", "batch-tracker"));
    }

    [Fact]
    public void The_address_pool_is_a_pool_and_an_advertisement_after_the_metallb_release()
    {
        var pool = Block("resource", "kubernetes_manifest", "pool");
        Assert.Contains("kind       = \"IPAddressPool\"", pool);
        Assert.Contains("name      = \"batchtracker\"", pool);
        Assert.Contains("namespace = \"metallb-system\"", pool);
        Assert.Contains("addresses = [var.load_balancer_range]", pool);
        Assert.Contains("helm_release.metallb", pool);
        var advertisement = Block("resource", "kubernetes_manifest", "advertisement");
        Assert.Contains("kind       = \"L2Advertisement\"", advertisement);
        Assert.Contains("name      = \"batchtracker\"", advertisement);
        Assert.Contains("namespace = \"metallb-system\"", advertisement);
        Assert.Contains("helm_release.metallb", advertisement);
    }

    [Fact]
    public void Provider_versions_are_exact_and_the_lock_file_holds_both()
    {
        var versions = Tf();
        foreach (var provider in new[] { "helm", "kubernetes" })
        {
            Assert.Matches($@"{provider}\s+= \{{\s*source\s+= ""hashicorp/{provider}""\s*version\s+= ""\d+\.\d+\.\d+""\s*\}}", versions);
        }
        var lock_ = File.ReadAllText(Path.Combine(TerraformDir, ".terraform.lock.hcl"));
        foreach (var provider in new[] { "helm", "kubernetes" })
        {
            var entry = Regex.Match(lock_, $@"provider ""registry.terraform.io/hashicorp/{provider}"" \{{.*?\n\}}", RegexOptions.Singleline);
            Assert.True(entry.Success, $"lock file has no {provider}");
            Assert.Contains("h1:", entry.Value);
        }
    }

    [Fact]
    public void State_and_the_provider_directory_are_gitignored()
    {
        var ignore = File.ReadAllLines(Path.Combine(Root(), "deploy", ".gitignore")).Select(l => l.Trim()).ToArray();
        Assert.Contains("terraform.tfstate*", ignore);
        Assert.Contains(".terraform/", ignore);
    }

    [Fact]
    public void Setup_runs_terraform_unattended_with_state_outside_the_release()
    {
        var run = Function("Invoke-Terraform");
        Assert.Contains("terraform init -input=false -lockfile=readonly", run);
        Assert.Contains("terraform apply -input=false -auto-approve", run);
        Assert.Contains("-state=", run);
        Assert.Contains("TF_DATA_DIR=", run);
        Assert.Contains("$StateDir", run);
        Assert.Contains("-var=generated_dir=", run);
        Assert.Contains("-var=load_balancer_range=$LoadBalancerRange", run);
    }

    [Fact]
    public void Setup_installs_no_chart_and_applies_no_manifest_itself()
    {
        Assert.DoesNotContain("upgrade --install", Setup);
        Assert.DoesNotContain("--create-namespace", Setup);
        Assert.DoesNotContain("kubectl apply", Setup);
        Assert.DoesNotContain("create namespace", Setup);
        Assert.Contains("$Helm template", Setup);
    }

    [Fact]
    public void A_chart_is_applied_then_rolled_out_one_chart_at_a_time()
    {
        var install = Function("Install-Chart");
        var apply = install.IndexOf("Invoke-Terraform @(\"helm_release.$name\")", StringComparison.Ordinal);
        var rollout = install.IndexOf("rollout status", StringComparison.Ordinal);
        Assert.True(apply >= 0, "Install-Chart does not apply its release");
        Assert.True(rollout > apply, "the rollout check must follow the apply");
        Assert.True(install.IndexOf("Write-GeneratedValues $name", StringComparison.Ordinal) is var written && written >= 0 && written < apply, "values must be written before the apply");
        Assert.Matches(@"foreach \(\$chart in \$Charts\) \{ Install-Chart \$chart \}\s+Set-LoadBalancerPool", Setup);
    }

    [Fact]
    public void The_pool_is_applied_only_with_a_range()
    {
        var pool = Function("Set-LoadBalancerPool");
        Assert.True(pool.IndexOf("[SKIP] MetalLB address pool", StringComparison.Ordinal) < pool.IndexOf("Invoke-Terraform", StringComparison.Ordinal));
        Assert.Contains("Invoke-Terraform @('kubernetes_manifest.pool', 'kubernetes_manifest.advertisement')", pool);
    }

    [Fact]
    public void The_app_secrets_still_travel_by_pipe_after_the_namespace_exists()
    {
        var secret = Function("Set-AppSecret");
        var namespaced = secret.IndexOf("Invoke-Terraform @('kubernetes_namespace_v1.batch-tracker')", StringComparison.Ordinal);
        var piped = secret.IndexOf("Get-SecretScript", StringComparison.Ordinal);
        Assert.True(namespaced >= 0 && piped > namespaced, "the namespace must exist before the secrets");
    }

    [Fact]
    public void The_app_is_checked_then_applied_with_the_secret_revision_in_its_values()
    {
        var app = Function("Install-App");
        Assert.Contains("$values['secretRevision'] = (Get-FileHash $AppEnvFile -Algorithm SHA256).Hash", app);
        var check = app.IndexOf("Assert-TrustedProxies", StringComparison.Ordinal);
        var apply = app.IndexOf("Invoke-Terraform @('helm_release.batch-tracker')", StringComparison.Ordinal);
        Assert.True(check >= 0 && apply > check, "the proxy check must run before the apply");
        Assert.True(app.IndexOf("Set-AppSecret", StringComparison.Ordinal) < apply, "secrets must exist before the apply");
    }
}
