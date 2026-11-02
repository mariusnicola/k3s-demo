$namespaces = @('metallb-system', 'ingress-nginx', 'prometheus', 'keda')

Test-Check 'REQ-BT-046 Prometheus runs on its bound volume with 120 days' {
    $claims = @((Get-KubeJson get pvc -n prometheus).items | Where-Object { $_.metadata.name -eq 'prometheus-server' -and $_.status.phase -eq 'Bound' })
    Assert-True ($claims.Count -eq 1) 'no bound claim'
    $pod = @((Get-KubeJson get pods -n prometheus).items | Where-Object { $_.metadata.name -match '^prometheus-server-' })[0]
    Assert-True (@($pod.spec.volumes | Where-Object { $_.persistentVolumeClaim.claimName -eq 'prometheus-server' }).Count -eq 1) 'the pod does not mount it'
    $arguments = @($pod.spec.containers | Where-Object { $_.name -eq 'prometheus-server' })[0].args -join ' '
    Assert-True ($arguments -match 'retention\.time=120d') "args: $arguments"
}

foreach ($namespace in $namespaces) {
    Test-Check "$namespace workloads fully ready" {
        $workloads = @((Get-KubeJson get 'deploy,ds' -n $namespace).items)
        Assert-True ($workloads.Count -gt 0) 'no workloads'
        foreach ($workload in $workloads) {
            $desired = if ($workload.kind -eq 'DaemonSet') { $workload.status.desiredNumberScheduled } else { $workload.spec.replicas }
            $ready = if ($workload.kind -eq 'DaemonSet') { $workload.status.numberReady } else { $workload.status.readyReplicas }
            Assert-True (($desired -gt 0) -and ($ready -eq $desired)) "$($workload.metadata.name) ready $ready of $desired"
        }
    }

    Test-Check "$namespace containers have memory limits" {
        $pods = @((Get-KubeJson get pods -n $namespace --field-selector=status.phase!=Succeeded).items)
        Assert-True ($pods.Count -gt 0) 'no pods'
        foreach ($pod in $pods) {
            $unlimited = @($pod.spec.containers | Where-Object { -not $_.resources.limits.memory })
            Assert-True ($unlimited.Count -eq 0) "$($pod.metadata.name) has no memory limit: $($unlimited.name -join ',')"
        }
    }
}

Test-Check 'Prometheus scrapes ingress-nginx' {
    $up = @(Get-PrometheusResult 'up{namespace="ingress-nginx"} == 1')
    Assert-True ($up.Count -gt 0) 'no healthy ingress-nginx target'
}

Test-Check 'ingress-nginx has an external address and answers' {
    $service = Get-KubeJson get service ingress-nginx-controller -n ingress-nginx
    $address = $service.status.loadBalancer.ingress.ip
    Assert-True ([bool]$address) 'no external address, MetalLB pool not set'
    Invoke-Probe tcp $address 80
}

Test-Check 'ingress connects on every source port container NAT holds' {
    $ports = @(Get-NatHeldPorts (Invoke-Wsl sudo -n cat /proc/net/nf_conntrack))
    Assert-True ($ports.Count -gt 0) 'no open container connection to the ingress'
    foreach ($port in $ports) { Invoke-Probe tcp $IngressAddress 80 $port }
}

Test-Check 'ingress ignores a spoofed address: one client builds one failure count' {
    Set-Content $FailureStamp (Get-Date).ToString('o')
    $answers = @(1..3 | ForEach-Object {
        Invoke-Wsl curl -s -o /dev/null -m 15 -w '%{http_code} %{time_total}' -H "X-Forwarded-For: 203.0.113.$_" "$IngressOrigin/line"
    })
    $gap = Get-SpoofGap $answers
    Assert-True ($gap -eq '') "$gap, answers: $($answers -join ', ')"
}

Test-Check 'metrics-server stays on the node in caps.json' {
    $wanted = @($Caps.reserved | Where-Object { $_.name -eq 'metrics-server' })[0].node
    $deployment = Get-KubeJson get deployment metrics-server -n kube-system
    Assert-True ($deployment.spec.template.spec.nodeSelector.'kubernetes.io/hostname' -eq $wanted) 'not pinned'
    $pods = @((Get-KubeJson get pods -n kube-system -l k8s-app=metrics-server).items)
    $wrong = @($pods | Where-Object { $_.spec.nodeName -ne $wanted })
    Assert-True ($pods.Count -gt 0 -and $wrong.Count -eq 0) "on $($pods.spec.nodeName -join ',')"
}
