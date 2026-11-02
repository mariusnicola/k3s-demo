$script:nodes = @()

function Get-NodeByName([string]$NodeName) {
    $node = $script:nodes | Where-Object { $_.metadata.name -eq $NodeName }
    Assert-True ($null -ne $node) "$NodeName not found"
    return $node
}

Test-Check 'cluster nodes are exactly the workers' {
    $script:nodes = @((Get-KubeJson get nodes).items)
    $expected = ($Workers | Sort-Object) -join ','
    $actual = ($script:nodes.metadata.name | Sort-Object) -join ','
    Assert-True ($actual -eq $expected) "found $actual"
}

Test-Check 'every worker Ready' {
    foreach ($worker in $Workers) {
        $ready = ((Get-NodeByName $worker).status.conditions | Where-Object { $_.type -eq 'Ready' }).status
        Assert-True ($ready -eq 'True') "$worker not Ready"
    }
}

Test-Check 'every worker on one version' {
    $versions = @($script:nodes.status.nodeInfo.kubeletVersion | Sort-Object -Unique)
    Assert-True ($versions.Count -eq 1) "versions: $($versions -join ', ')"
}

Test-Check 'workers accept app pods' {
    foreach ($worker in $Workers) {
        Assert-True ($null -eq (Get-NodeByName $worker).spec.taints) "$worker is tainted"
    }
}

Test-Check 'system pods running' {
    $systemPods = @((Get-KubeJson get pods -n kube-system).items)
    Assert-True ($systemPods.Count -gt 0) 'no system pods'
    foreach ($pod in $systemPods) {
        Assert-True ($pod.status.phase -in 'Running', 'Succeeded') "$($pod.metadata.name) is $($pod.status.phase)"
    }
}

Test-Check 'API server reaches metrics-server' { Invoke-Kubectl get --raw /apis/metrics.k8s.io/v1beta1/nodes | Out-Null }

Test-Check 'Helm reaches the cluster' { Invoke-Helm list -A | Out-Null }
